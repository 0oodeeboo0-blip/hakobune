using System.Buffers;
using System.Net.Sockets;
using Hakobune.Core.Transfers;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace Hakobune.Core.Remote.Sftp;

/// <summary>
/// SFTP の接続 1 本（SSH.NET を使う）。
/// </summary>
/// <remarks>
/// やり直しやつなぎ直しは <see cref="TransferScheduler"/> が受け持つので、ここでは行わない。
/// 一時的な失敗のあとは <see cref="IsConnected"/> を false にして、使い回されないようにする。
/// </remarks>
public sealed class SftpRemoteSession : IRemoteSession
{
    private const int BufferSize = 81920;

    private readonly SftpConnectionSettings _settings;
    private SftpClient? _client;
    private PrivateKeyFile? _keyFile;
    private bool _broken;
    private bool _hostKeyRejected;

    public SftpRemoteSession(SftpConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public bool IsConnected => !_broken && _client is { IsConnected: true };

    // SFTP はどれも基本の機能として持っている。
    public RemoteCapabilities Capabilities { get; } = new(CanResumeUpload: true, CanResumeDownload: true, CanSetModifiedTime: true);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            throw new InvalidOperationException("この接続はすでに使われています。");

        var client = new SftpClient(CreateConnectionInfo())
        {
            OperationTimeout = _settings.OperationTimeout,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = _settings.ValidateHostKey(e.HostKeyName, e.FingerPrintSHA256, e.KeyLength);
            if (!e.CanTrust)
                _hostKeyRejected = true;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            client.Dispose();
            if (_hostKeyRejected)
            {
                throw new RemoteOperationException(
                    TransferFailure.Permanent(FailureReason.CertificateRejected, "サーバーの鍵を受け入れませんでした"), null, ex);
            }
            throw SftpErrors.Translate(ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
    }

    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RequireClient().WorkingDirectory);

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            var items = new List<RemoteItem>();
            await foreach (var file in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                    continue;
                items.Add(ToRemoteItem(file.Name, file.FullName, file.Attributes));
            }
            return items;
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
    }

    public async Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            var attributes = await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return ToRemoteItem(RemotePath.GetFileName(path), path, attributes);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            // SFTP には親フォルダごと作るコマンドが無いので、上から順に、無いものだけ作る。
            var current = path.StartsWith('/') ? "/" : "";
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? segment : RemotePath.Combine(current, segment);
                if (await client.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                    continue;
                try
                {
                    await client.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (SshException)
                {
                    // ほかの接続が同時に作ったのなら問題ない。
                    if (!await client.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                        throw;
                }
            }
        }
        catch (Exception ex) when (!IsOurCancellation(ex, cancellationToken))
        {
            throw Fail(ex);
        }
    }

    public async Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var client = RequireClient();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // 最初からなら作り直し、途中からなら開いて続きの位置に書く。
            var remote = await client.OpenAsync(remotePath, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, cancellationToken).ConfigureAwait(false);
            await using (remote.ConfigureAwait(false))
            {
                if (offset > 0)
                    remote.Seek(offset, SeekOrigin.Begin);

                long sent = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await remote.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sent += read;
                    progress?.Report(sent);
                }
                await remote.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
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
        }
    }

    public async Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var client = RequireClient();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remote = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            await using (remote.ConfigureAwait(false))
            {
                if (offset > 0)
                    remote.Seek(offset, SeekOrigin.Begin);

                long received = 0;
                int read;
                while ((read = await remote.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(received);
                }
            }
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
        }
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(client => client.DeleteFileAsync(path, cancellationToken), cancellationToken);

    public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken) =>
        RunAsync(client => client.RenameFileAsync(fromPath, toPath, cancellationToken), cancellationToken);

    public Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) =>
        RunAsync(client => Task.Run(() => client.SetLastWriteTimeUtc(path, modified.UtcDateTime), cancellationToken), cancellationToken);

    // 何もしていない間は SSH.NET が KeepAliveInterval ごとに合図を送る。
    // ここでは軽い問い合わせをして、接続がまだ使えるかも確かめる。
    public Task KeepAliveAsync(CancellationToken cancellationToken) =>
        RunAsync(client => client.GetAttributesAsync(client.WorkingDirectory, cancellationToken), cancellationToken);

    public ValueTask DisposeAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            if (!_broken && client.IsConnected)
            {
                try
                {
                    client.Disconnect();
                }
                catch (Exception)
                {
                    // 切断のあいさつに失敗しても問題ない。
                }
            }
            client.Dispose();
        }
        _keyFile?.Dispose();
        _keyFile = null;
        return ValueTask.CompletedTask;
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(_settings.PrivateKeyPath))
        {
            _keyFile = string.IsNullOrEmpty(_settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_settings.PrivateKeyPath)
                : new PrivateKeyFile(_settings.PrivateKeyPath, _settings.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(_settings.UserName, _keyFile));
        }
        if (_settings.Password is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(_settings.UserName, password));

            // パスワードを keyboard-interactive で尋ねるサーバー向け
            var interactive = new KeyboardInteractiveAuthenticationMethod(_settings.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                        prompt.Response = password;
                }
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0)
            throw new InvalidOperationException("パスワードか秘密鍵を指定してください。");

        var info = new ConnectionInfo(_settings.Host, _settings.Port, _settings.UserName, [.. methods])
        {
            Timeout = _settings.ConnectTimeout,
        };
        if (_settings.FileNameEncoding is { } encoding)
            info.Encoding = encoding;
        return info;
    }

    private static RemoteItem ToRemoteItem(string name, string fullPath, SftpFileAttributes attributes)
    {
        var type = attributes.IsDirectory
            ? RemoteItemType.Directory
            : attributes.IsSymbolicLink ? RemoteItemType.Link : RemoteItemType.File;
        DateTimeOffset? modified = attributes.LastWriteTimeUtc == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(attributes.LastWriteTimeUtc, DateTimeKind.Utc));
        var size = type == RemoteItemType.Directory ? 0 : attributes.Size;
        return new RemoteItem(name, fullPath, type, size, modified, FormatPermissions(attributes));
    }

    private static string FormatPermissions(SftpFileAttributes a) => string.Concat(
        a.OwnerCanRead ? "r" : "-", a.OwnerCanWrite ? "w" : "-", a.OwnerCanExecute ? "x" : "-",
        a.GroupCanRead ? "r" : "-", a.GroupCanWrite ? "w" : "-", a.GroupCanExecute ? "x" : "-",
        a.OthersCanRead ? "r" : "-", a.OthersCanWrite ? "w" : "-", a.OthersCanExecute ? "x" : "-");

    private async Task RunAsync(Func<SftpClient, Task> operation, CancellationToken cancellationToken)
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

    private SftpClient RequireClient()
    {
        if (_client is null || _broken)
            throw new RemoteOperationException(TransferFailure.Transient(FailureReason.ConnectionLost, "接続されていません"));
        return _client;
    }

    private RemoteOperationException Fail(Exception exception)
    {
        var translated = SftpErrors.Translate(exception);
        if (translated.Failure.IsTransient || translated.Failure.Reason == FailureReason.Unknown)
            _broken = true;
        return translated;
    }

    private static bool IsOurCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}

/// <summary>SSH.NET のエラーを、Hakobune の失敗に置き換える。</summary>
internal static class SftpErrors
{
    public static RemoteOperationException Translate(Exception exception) => exception switch
    {
        RemoteOperationException remote => remote,

        SshAuthenticationException or SshPassPhraseNullOrEmptyException =>
            Wrap(TransferFailure.Permanent(FailureReason.AuthenticationFailed, exception.Message), exception),
        SshConnectionException { DisconnectReason: DisconnectReason.HostKeyNotVerifiable } =>
            Wrap(TransferFailure.Permanent(FailureReason.CertificateRejected, exception.Message), exception),
        SshConnectionException { DisconnectReason: DisconnectReason.TooManyConnections } =>
            Wrap(TransferFailure.Transient(FailureReason.TooManyConnections, exception.Message), exception),
        SshConnectionException or ProxyException =>
            Wrap(TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message), exception),
        SshOperationTimeoutException =>
            Wrap(TransferFailure.Transient(FailureReason.Timeout, exception.Message), exception),

        SftpPermissionDeniedException =>
            Wrap(TransferFailure.Permanent(FailureReason.PermissionDenied, exception.Message), exception),
        SftpPathNotFoundException =>
            Wrap(TransferFailure.Permanent(FailureReason.NotFound, exception.Message), exception),
        SftpException { StatusCode: StatusCode.NoConnection or StatusCode.ConnectionLost } =>
            Wrap(TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message), exception),
        SftpException sftp when sftp.Message.Contains("space", StringComparison.OrdinalIgnoreCase) =>
            Wrap(TransferFailure.Permanent(FailureReason.InsufficientStorage, exception.Message), exception),
        SftpException =>
            Wrap(TransferFailure.Permanent(FailureReason.Unknown, exception.Message), exception),

        // そのほかの SSH のエラーは、接続がおかしくなったものとして扱う。
        SshException =>
            Wrap(TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message), exception),

        TimeoutException or OperationCanceledException =>
            Wrap(TransferFailure.Transient(FailureReason.Timeout, exception.Message), exception),
        SocketException or IOException or ObjectDisposedException =>
            Wrap(TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message), exception),

        _ => Wrap(TransferFailure.Permanent(FailureReason.Unknown, exception.Message), exception),
    };

    private static RemoteOperationException Wrap(TransferFailure failure, Exception exception) =>
        new(failure, exception.Message, exception);
}

/// <summary>SFTP の接続を作る。</summary>
public sealed class SftpSessionFactory(SftpConnectionSettings settings) : IRemoteSessionFactory
{
    public IRemoteSession Create() => new SftpRemoteSession(settings);
}
