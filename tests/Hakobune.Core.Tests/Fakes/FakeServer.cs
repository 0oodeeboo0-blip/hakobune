using System.Collections.Concurrent;
using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Fakes;

/// <summary>
/// 転送の途中で起こす障害。<paramref name="BytesToWrite"/> バイトだけ書いたところで
/// <paramref name="Failure"/> を起こす（null なら何も起こさず、そのまま成功したように振る舞う）。
/// </summary>
internal sealed record WriteFault(long BytesToWrite, TransferFailure? Failure);

/// <summary>
/// テスト用のサーバー。障害を自由に起こせる。
/// </summary>
internal sealed class FakeServer : IRemoteSessionFactory
{
    private readonly object _gate = new();
    private int _connectedSessions;
    private int _maxConnectedSessions;
    private int _sessionsCreated;
    private int _connectAttempts;

    public InMemoryRemoteFileSystem Files { get; } = new();

    public RemoteCapabilities Capabilities { get; set; } = new(CanResumeUpload: true, CanResumeDownload: true, CanSetModifiedTime: true);

    /// <summary>同時に接続できる数。超えると「421 Too many connections」を返す。</summary>
    public int MaxSessions { get; set; } = int.MaxValue;

    /// <summary>接続のたびに呼ばれる。失敗させたいときは失敗を返す。</summary>
    public Func<TransferFailure?>? ConnectFault { get; set; }

    /// <summary>アップロードのたびに呼ばれる（パス、開始位置、送られてきたバイト数）。</summary>
    public Func<string, long, long, WriteFault?>? UploadFault { get; set; }

    /// <summary>ダウンロードのたびに呼ばれる（パス、開始位置、残りのバイト数）。</summary>
    public Func<string, long, long, WriteFault?>? DownloadFault { get; set; }

    /// <summary>アップロードの最初に待たせたいとき（中止のテスト用）。</summary>
    public Func<CancellationToken, Task>? BeforeUpload { get; set; }

    public int SessionsCreated => Volatile.Read(ref _sessionsCreated);

    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    public int MaxConnectedSessions
    {
        get
        {
            lock (_gate)
                return _maxConnectedSessions;
        }
    }

    /// <summary>アップロードの呼び出し（パスと開始位置）。</summary>
    public ConcurrentQueue<(string Path, long Offset)> Uploads { get; } = new();

    /// <summary>成功したアップロードの順番。</summary>
    public ConcurrentQueue<string> CompletedUploads { get; } = new();

    /// <summary>ダウンロードの呼び出し（パスと開始位置）。</summary>
    public ConcurrentQueue<(string Path, long Offset)> Downloads { get; } = new();

    public IRemoteSession Create()
    {
        Interlocked.Increment(ref _sessionsCreated);
        return new FakeRemoteSession(this);
    }

    internal void Connect()
    {
        Interlocked.Increment(ref _connectAttempts);
        if (ConnectFault?.Invoke() is { } failure)
            throw new RemoteOperationException(failure);

        lock (_gate)
        {
            if (_connectedSessions >= MaxSessions)
                throw new RemoteOperationException(FtpReplyClassifier.Classify(421, "Too many connections"));
            _connectedSessions++;
            _maxConnectedSessions = Math.Max(_maxConnectedSessions, _connectedSessions);
        }
    }

    internal void Disconnect()
    {
        lock (_gate)
            _connectedSessions--;
    }
}

/// <summary>テスト用のサーバーへの接続。</summary>
internal sealed class FakeRemoteSession(FakeServer server) : IRemoteSession
{
    private bool _connected;

    public bool IsConnected => _connected;

    public RemoteCapabilities Capabilities => server.Capabilities;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        server.Connect();
        _connected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return Task.FromResult(server.Files.List(path));
    }

    public Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return Task.FromResult(server.Files.GetItem(path));
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConnected();
        server.Files.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public async Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (server.BeforeUpload is { } before)
            await before(cancellationToken);

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        server.Uploads.Enqueue((remotePath, offset));

        var fault = server.UploadFault?.Invoke(remotePath, offset, data.Length);
        var count = fault is null ? data.Length : (int)Math.Clamp(fault.BytesToWrite, 0, data.Length);
        if (fault is null || count > 0)
            server.Files.WriteFile(remotePath, data.AsSpan(0, count), offset, DateTimeOffset.UtcNow);
        progress?.Report(count);

        if (fault?.Failure is { } failure)
            Throw(failure);
        server.CompletedUploads.Enqueue(remotePath);
    }

    public async Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var data = server.Files.ReadFile(remotePath)
            ?? throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, remotePath));
        server.Downloads.Enqueue((remotePath, offset));

        var remaining = data.Length - (int)offset;
        var fault = server.DownloadFault?.Invoke(remotePath, offset, remaining);
        var count = fault is null ? remaining : (int)Math.Clamp(fault.BytesToWrite, 0, remaining);
        await destination.WriteAsync(data.AsMemory((int)offset, count), cancellationToken);
        progress?.Report(count);

        if (fault?.Failure is { } failure)
            Throw(failure);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConnected();
        server.Files.Delete(path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        EnsureConnected();
        server.Files.Rename(fromPath, toPath);
        return Task.CompletedTask;
    }

    public Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        EnsureConnected();
        server.Files.SetModified(path, modified);
        return Task.CompletedTask;
    }

    public Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Drop();
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!_connected)
            throw new RemoteOperationException(TransferFailure.Transient(FailureReason.ConnectionLost, "接続されていません"));
    }

    // 一時的な失敗のあとは、本物の接続と同じく使えなくなる。
    private void Throw(TransferFailure failure)
    {
        if (failure.IsTransient)
            Drop();
        throw new RemoteOperationException(failure);
    }

    private void Drop()
    {
        if (!_connected)
            return;
        _connected = false;
        server.Disconnect();
    }
}
