using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Remote.Ftp;

/// <summary>
/// FTP / FTPS の接続 1 本（FluentFTP を使う）。
/// </summary>
/// <remarks>
/// やり直しやつなぎ直しは <see cref="TransferScheduler"/> が受け持つので、ここでは行わない。
/// 一時的な失敗のあとは <see cref="IsConnected"/> を false にして、使い回されないようにする。
/// </remarks>
public sealed class FtpRemoteSession : IRemoteSession
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private readonly FtpConnectionSettings _settings;
    private AsyncFtpClient? _client;
    private bool _broken;

    public FtpRemoteSession(FtpConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public bool IsConnected => !_broken && _client is { IsConnected: true };

    public RemoteCapabilities Capabilities { get; private set; } = RemoteCapabilities.None;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            throw new InvalidOperationException("この接続はすでに使われています。");

        var client = new AsyncFtpClient(_settings.Host, _settings.UserName, _settings.Password, _settings.Port, CreateConfig());
        // 既定は UTF-8。サーバーが UTF8 に対応していないと名乗っても、名前は UTF-8 として送る（古いサーバーは設定で切り替える）。
        client.Encoding = _settings.FileNameEncoding ?? Encoding.UTF8;
        if (_settings.ValidateCertificate is { } validate)
            client.ValidateCertificate += (_, e) => e.Accept = validate(e.Certificate, e.Chain, e.PolicyErrors);

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw FtpErrors.Translate(ex);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _client = client;
        Capabilities = new RemoteCapabilities(
            CanResumeUpload: true, // APPE は FTP の基本コマンド
            CanResumeDownload: client.HasFeature(FtpCapability.REST),
            CanSetModifiedTime: client.HasFeature(FtpCapability.MFMT) || client.HasFeature(FtpCapability.MDTM));
    }

    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) =>
        RunAsync(client => client.GetWorkingDirectory(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<RemoteItem>>(async client =>
        {
            var listing = await client.GetListing(path, FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
            var items = new List<RemoteItem>(listing.Length);
            foreach (var entry in listing)
            {
                if (entry.Name is "." or "..")
                    continue;
                items.Add(ToRemoteItem(entry));
            }
            return items;
        }, cancellationToken);

    public Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken) =>
        RunAsync<RemoteItem?>(async client =>
        {
            if (client.HasFeature(FtpCapability.MLST) || !client.HasFeature(FtpCapability.SIZE))
            {
                var info = await client.GetObjectInfo(path, dateModified: false, cancellationToken).ConfigureAwait(false);
                return info is null ? null : ToRemoteItem(info, path);
            }

            // MLST に対応していないサーバー（vsftpd など）では、親フォルダの一覧を取らずに SIZE と MDTM で調べる。
            // 一覧を取ると、たくさんのファイルを送るときに 1 件ごとに一覧全体を受け取ることになり遅い。
            var size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
            if (size < 0)
                return null;

            DateTimeOffset? modified = null;
            if (client.HasFeature(FtpCapability.MDTM))
            {
                var time = await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
                if (time != DateTime.MinValue)
                    modified = AsUtc(time);
            }
            return new RemoteItem(RemotePath.GetFileName(path), path, RemoteItemType.File, size, modified);
        }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(client => client.CreateDirectory(path, force: true, cancellationToken), cancellationToken);

    public async Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var client = RequireClient();
        Stream? data = null;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // 最初からなら STOR（上書き）、途中からなら APPE（続きを足す）。
            data = offset > 0
                ? await client.OpenAppend(remotePath, FtpDataType.Binary, checkIfFileExists: false, cancellationToken).ConfigureAwait(false)
                : await client.OpenWrite(remotePath, FtpDataType.Binary, checkIfFileExists: false, cancellationToken).ConfigureAwait(false);

            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await data.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sent += read;
                progress?.Report(sent);
            }
            await data.FlushAsync(cancellationToken).ConfigureAwait(false);

            // データ用の接続を閉じてから、転送完了の応答（226 など）を受け取る。
            await data.DisposeAsync().ConfigureAwait(false);
            data = null;
            await EnsureTransferCompletedAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
        catch
        {
            // 中止された転送のあとは、制御用の接続の状態が分からないので使わない。
            _broken = true;
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (data is not null)
                await DisposeQuietlyAsync(data).ConfigureAwait(false);
        }
    }

    public async Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var client = RequireClient();
        Stream? data = null;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // 途中からのときは REST で開始位置を伝える。
            data = await client.OpenRead(remotePath, FtpDataType.Binary, offset, checkIfFileExists: false, cancellationToken).ConfigureAwait(false);

            long received = 0;
            int read;
            while ((read = await data.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(received);
            }

            await data.DisposeAsync().ConfigureAwait(false);
            data = null;
            await EnsureTransferCompletedAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
        catch
        {
            _broken = true;
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (data is not null)
                await DisposeQuietlyAsync(data).ConfigureAwait(false);
        }
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(client => client.DeleteFile(path, cancellationToken), cancellationToken);

    public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken) =>
        RunAsync(client => client.Rename(fromPath, toPath, cancellationToken), cancellationToken);

    public Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) =>
        RunAsync(client => client.SetModifiedTime(path, modified.UtcDateTime, cancellationToken), cancellationToken);

    public Task KeepAliveAsync(CancellationToken cancellationToken) =>
        RunAsync(async client =>
        {
            var reply = await client.Execute("NOOP", cancellationToken).ConfigureAwait(false);
            if (!reply.Success)
                throw FtpErrors.FromReply(reply);
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
            return;

        if (!_broken && client.IsConnected)
        {
            using var timeout = new CancellationTokenSource(QuitTimeout);
            try
            {
                await client.Disconnect(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 切断のあいさつ（QUIT）に失敗しても問題ない。
            }
        }
        await client.DisposeAsync().ConfigureAwait(false);
    }

    private FtpConfig CreateConfig() => new()
    {
        EncryptionMode = _settings.Security switch
        {
            FtpSecurity.Explicit => FtpEncryptionMode.Explicit,
            FtpSecurity.Implicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        },
        DataConnectionType = _settings.UsePassiveMode ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
        ConnectTimeout = ToMilliseconds(_settings.ConnectTimeout),
        ReadTimeout = ToMilliseconds(_settings.ReadTimeout),
        DataConnectionConnectTimeout = ToMilliseconds(_settings.ConnectTimeout),
        DataConnectionReadTimeout = ToMilliseconds(_settings.ReadTimeout),
        SocketKeepAlive = true,
        // やり直しは Hakobune 側で行うので、FluentFTP の中では繰り返さない。
        RetryAttempts = 1,
        // 接続の維持は Hakobune 側で行う。
        Noop = false,
        TimeConversion = FtpDate.ServerTime,
    };

    private static int ToMilliseconds(TimeSpan value) => (int)Math.Clamp(value.TotalMilliseconds, 1, int.MaxValue);

    private static RemoteItem ToRemoteItem(FtpListItem entry, string? fullPath = null)
    {
        var type = entry.Type == FtpObjectType.Directory
            ? RemoteItemType.Directory
            : string.IsNullOrEmpty(entry.LinkTarget) ? RemoteItemType.File : RemoteItemType.Link;
        DateTimeOffset? modified = entry.Modified == DateTime.MinValue ? null : AsUtc(entry.Modified);
        var size = type == RemoteItemType.Directory ? 0 : entry.Size;
        var path = fullPath ?? entry.FullName ?? entry.Name;
        var name = string.IsNullOrEmpty(entry.Name) ? RemotePath.GetFileName(path) : RemotePath.GetFileName(entry.Name);
        return new RemoteItem(name, path, type, size, modified, entry.RawPermissions);
    }

    // MLSD・MLST・MDTM の時刻は UTC。LIST の時刻はサーバーの地域の時刻だが、ここでは区別しない（接続先ごとの時差の設定で補う予定）。
    private static DateTimeOffset AsUtc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));

    private static async Task EnsureTransferCompletedAsync(AsyncFtpClient client, CancellationToken cancellationToken)
    {
        var reply = await client.GetReply(cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
            throw FtpErrors.FromReply(reply);
    }

    private async Task RunAsync(Func<AsyncFtpClient, Task> operation, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            await operation(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
    }

    private async Task<T> RunAsync<T>(Func<AsyncFtpClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            return await operation(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
    }

    private AsyncFtpClient RequireClient()
    {
        if (_client is null || _broken)
            throw new RemoteOperationException(TransferFailure.Transient(FailureReason.ConnectionLost, "接続されていません"));
        return _client;
    }

    // 失敗を Hakobune の形に置き換え、一時的な失敗や想定外のエラーならこの接続を使えなくする。
    private RemoteOperationException Fail(Exception exception)
    {
        var translated = FtpErrors.Translate(exception);
        if (translated.Failure.IsTransient || translated.Failure.Reason == FailureReason.Unknown)
            _broken = true;
        return translated;
    }

    private static bool IsOurCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static async Task DisposeQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 失敗した転送の後片付け。接続はもう使わないので、ここでの失敗は無視する。
        }
    }
}

/// <summary>FTP・FluentFTP のエラーを、Hakobune の失敗に置き換える。</summary>
internal static class FtpErrors
{
    public static RemoteOperationException Translate(Exception exception)
    {
        switch (exception)
        {
            case RemoteOperationException remote:
                return remote;

            // サーバーが返した応答コード（4xx / 5xx）がある。
            case FtpCommandException command when TryGetFailureCode(command.CompletionCode, out var code):
                return new RemoteOperationException(FtpReplyClassifier.Classify(code, command.Message), command.Message, command);

            case FtpInvalidCertificateException:
                return Wrap(TransferFailure.Permanent(FailureReason.CertificateRejected, exception.Message), exception);

            case FtpSecurityNotAvailableException:
            case AuthenticationException:
                return Wrap(TransferFailure.Permanent(FailureReason.SecureConnectionFailed, exception.Message), exception);

            case FtpMissingSocketException:
                return Wrap(TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message), exception);

            // FluentFTP が包んだ例外は、中身で判断する。
            case FtpException { InnerException: { } inner }:
                return Translate(inner);

            case TimeoutException:
            case OperationCanceledException:
                return Wrap(TransferFailure.Transient(FailureReason.Timeout, exception.Message), exception);

            case SocketException:
            case IOException:
            case ObjectDisposedException:
                return Wrap(TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message), exception);

            default:
                return Wrap(TransferFailure.Permanent(FailureReason.Unknown, exception.Message), exception);
        }
    }

    public static RemoteOperationException FromReply(FtpReply reply)
    {
        if (TryGetFailureCode(reply.Code, out var code))
            return new RemoteOperationException(FtpReplyClassifier.Classify(code, reply.Message));

        return new RemoteOperationException(TransferFailure.Transient(
            FailureReason.ConnectionLost,
            $"サーバーから予期しない応答がありました: {reply.Code} {reply.Message}".TrimEnd()));
    }

    private static bool TryGetFailureCode(string? text, out int code) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code is >= 400 and <= 599;

    private static RemoteOperationException Wrap(TransferFailure failure, Exception exception) =>
        new(failure, exception.Message, exception);
}

/// <summary>FTP / FTPS の接続を作る。</summary>
public sealed class FtpSessionFactory(FtpConnectionSettings settings) : IRemoteSessionFactory
{
    /// <summary>接続先の設定。</summary>
    public FtpConnectionSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public IRemoteSession Create() => new FtpRemoteSession(Settings);
}
