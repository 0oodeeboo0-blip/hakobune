using Hakobune.Core.Remote;

namespace Hakobune.Core.Transfers;

/// <summary>実行中の項目の様子を、送信待ちの管理側に知らせる。</summary>
internal interface ITransferSink
{
    void SetTotalBytes(TransferItem item, long totalBytes);

    void MarkTargetWritten(TransferItem item);

    void ReportProgress(TransferItem item, long transferredBytes);

    Task EnsureRemoteDirectoryAsync(IRemoteSession session, string directory, CancellationToken cancellationToken);
}

/// <summary>1 回の試行の結果（完了またはスキップ）。失敗は例外で知らせる。</summary>
internal readonly record struct ExecutionResult(TransferState State, string? Note, bool SizeVerified)
{
    public static ExecutionResult Completed(bool sizeVerified) => new(TransferState.Completed, null, sizeVerified);

    public static ExecutionResult Skipped(string note) => new(TransferState.Skipped, note, false);
}

/// <summary>
/// 1 件を 1 回だけ転送してみる。やり直すかどうかは呼び出し側（<see cref="TransferScheduler"/>）が決める。
/// </summary>
internal sealed class TransferExecutor(TransferOptions options)
{
    private const int BufferSize = 81920;

    public Task<ExecutionResult> ExecuteAsync(TransferItem item, IRemoteSession session, ITransferSink sink, CancellationToken cancellationToken) =>
        item.Request.Direction == TransferDirection.Upload
            ? UploadAsync(item, session, sink, cancellationToken)
            : DownloadAsync(item, session, sink, cancellationToken);

    private async Task<ExecutionResult> UploadAsync(TransferItem item, IRemoteSession session, ITransferSink sink, CancellationToken cancellationToken)
    {
        var request = item.Request;
        var local = new FileInfo(request.LocalPath);
        if (!local.Exists)
            throw new FileNotFoundException("パソコン側のファイルが見つかりません。", request.LocalPath);

        var length = local.Length;
        sink.SetTotalBytes(item, length);

        var parent = RemotePath.GetParent(request.RemotePath);
        if (parent.Length > 0)
            await sink.EnsureRemoteDirectoryAsync(session, parent, cancellationToken).ConfigureAwait(false);

        long offset = 0;
        var existing = await session.GetItemAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IsDirectory)
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NameConflictsWithDirectory, request.RemotePath));

            if (item.HasWrittenTarget)
            {
                // 前回の試行で自分が書きかけたファイル。続きから送れるなら続きから送る。
                if (session.Capabilities.CanResumeUpload && existing.Size > 0 && existing.Size < length)
                    offset = existing.Size;
            }
            else
            {
                switch (request.OnConflict)
                {
                    case ConflictPolicy.Skip:
                        return ExecutionResult.Skipped("同じ名前のファイルがあるため飛ばしました");
                    case ConflictPolicy.OverwriteIfNewer
                        when existing.Modified is { } remoteModified && local.LastWriteTimeUtc <= remoteModified.UtcDateTime:
                        return ExecutionResult.Skipped("サーバーのほうが新しいため飛ばしました");
                }
            }
        }

        var stream = new FileStream(request.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            if (offset > 0)
                stream.Seek(offset, SeekOrigin.Begin);

            sink.MarkTargetWritten(item);
            sink.ReportProgress(item, offset);
            var progress = new SyncProgress<long>(sent => sink.ReportProgress(item, offset + sent));
            await session.UploadAsync(stream, request.RemotePath, offset, progress, cancellationToken).ConfigureAwait(false);
        }

        if (options.VerifySize)
        {
            var uploaded = await session.GetItemAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
            if (uploaded is null || uploaded.Size != length)
            {
                throw new RemoteOperationException(TransferFailure.Transient(
                    FailureReason.VerificationFailed,
                    $"送った大きさは {length:N0} バイトですが、サーバー上は {(uploaded?.Size ?? 0):N0} バイトでした"));
            }
        }

        if (options.PreserveModifiedTime && session.Capabilities.CanSetModifiedTime)
            await TrySetRemoteModifiedTimeAsync(session, request.RemotePath, local.LastWriteTimeUtc, cancellationToken).ConfigureAwait(false);

        sink.ReportProgress(item, length);
        return ExecutionResult.Completed(sizeVerified: options.VerifySize);
    }

    private async Task<ExecutionResult> DownloadAsync(TransferItem item, IRemoteSession session, ITransferSink sink, CancellationToken cancellationToken)
    {
        var request = item.Request;
        var remote = await session.GetItemAsync(request.RemotePath, cancellationToken).ConfigureAwait(false)
            ?? throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, request.RemotePath));
        if (remote.IsDirectory)
            throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NameConflictsWithDirectory, request.RemotePath));

        var length = remote.Size;
        sink.SetTotalBytes(item, Math.Max(length, 0));

        var finalPath = Path.GetFullPath(request.LocalPath);
        var partPath = finalPath + options.PartialDownloadSuffix;
        var local = new FileInfo(finalPath);
        if (local.Exists && !item.HasWrittenTarget)
        {
            switch (request.OnConflict)
            {
                case ConflictPolicy.Skip:
                    return ExecutionResult.Skipped("同じ名前のファイルがあるため飛ばしました");
                case ConflictPolicy.OverwriteIfNewer
                    when remote.Modified is { } remoteModified && remoteModified.UtcDateTime <= local.LastWriteTimeUtc:
                    return ExecutionResult.Skipped("パソコンのほうが新しいため飛ばしました");
            }
        }

        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // 書きかけは別の名前で置き、終わってから入れ替える。途中で失敗しても元のファイルは壊れない。
        long offset = 0;
        var part = new FileInfo(partPath);
        if (item.HasWrittenTarget && part.Exists && session.Capabilities.CanResumeDownload && part.Length > 0 && part.Length < length)
            offset = part.Length;

        var stream = new FileStream(partPath, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            if (offset > 0)
                stream.Seek(offset, SeekOrigin.Begin);

            sink.MarkTargetWritten(item);
            sink.ReportProgress(item, offset);
            var progress = new SyncProgress<long>(received => sink.ReportProgress(item, offset + received));
            await session.DownloadAsync(request.RemotePath, stream, offset, progress, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var downloaded = new FileInfo(partPath).Length;
        if (options.VerifySize && length >= 0 && downloaded != length)
        {
            throw new RemoteOperationException(TransferFailure.Transient(
                FailureReason.VerificationFailed,
                $"サーバー上の大きさは {length:N0} バイトですが、受け取ったのは {downloaded:N0} バイトでした"));
        }

        File.Move(partPath, finalPath, overwrite: true);
        if (options.PreserveModifiedTime && remote.Modified is { } modified)
            File.SetLastWriteTimeUtc(finalPath, modified.UtcDateTime);

        sink.ReportProgress(item, downloaded);
        return ExecutionResult.Completed(sizeVerified: options.VerifySize && length >= 0);
    }

    private static async Task TrySetRemoteModifiedTimeAsync(IRemoteSession session, string path, DateTime modifiedUtc, CancellationToken cancellationToken)
    {
        try
        {
            await session.SetModifiedTimeAsync(path, new DateTimeOffset(modifiedUtc, TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteOperationException ex) when (!ex.Failure.IsTransient)
        {
            // 日時を設定できないサーバーもある。ファイルそのものは届いているので、失敗にはしない。
        }
    }
}
