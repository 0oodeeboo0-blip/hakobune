using System.Collections.Concurrent;
using Hakobune.Core.Connections;
using Hakobune.Core.Remote;
using Hakobune.Core.Remote.Ftp;
using Hakobune.Core.Remote.Sftp;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Backups;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Connections;

/// <summary>パスワードとサーバーの鍵を確かめる、小さな偽のサーバー。</summary>
internal sealed class FakeLoginServer
{
    public string Password { get; set; } = "right";

    public string HostKeyFingerprint { get; set; } = "AAAA";

    public string Home { get; set; } = "/home/web";

    public Dictionary<string, List<RemoteItem>> Folders { get; } = new(StringComparer.Ordinal);

    /// <summary>ファイルの中身（アップロードで書かれ、ダウンロードで読まれる）。</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>アップロードの最初に待たせたいとき。</summary>
    public Func<CancellationToken, Task>? BeforeUpload { get; set; }

    /// <summary>画面からの操作の記録（作ったフォルダ、名前の変更、削除、属性の変更）。</summary>
    public ConcurrentQueue<string> Operations { get; } = new();

    public TransferFailure? ConnectFault { get; set; }

    /// <summary>暗号化（FTPS）に対応していないサーバーのまね（FTPS で入ろうとすると断る）。</summary>
    public bool RefusesEncryption { get; set; }

    /// <summary>暗号化（FTPS）でないと入れないサーバーのまね（ロリポップ！など。暗号化なしの FTP を断る）。</summary>
    public bool RequiresEncryption { get; set; }

    /// <summary>接続しようとした方式の記録。</summary>
    public List<ConnectionProtocol> ProtocolsTried { get; } = [];

    /// <summary>接続の前に待つ（遅いサーバーのまね）。</summary>
    public Func<ConnectionProfile, CancellationToken, Task>? Delay { get; set; }

    /// <summary>秘密鍵で入るときの、鍵のパスフレーズ（null なら掛かっていない）。</summary>
    public string? KeyPassphrase { get; set; }

    public int Logins { get; private set; }

    public List<string?> PasswordsTried { get; } = [];

    public SessionFactoryBuilder Builder => (profile, credentials, validateHostKey, validateCertificate, protocolLog) =>
        new Factory(this, profile, credentials, validateHostKey, protocolLog);

    private sealed class Factory(
        FakeLoginServer server,
        ConnectionProfile profile,
        ConnectionCredentials credentials,
        HostKeyValidator validateHostKey,
        Action<string>? protocolLog) : IRemoteSessionFactory
    {
        public IRemoteSession Create() => new Session(server, profile, credentials, validateHostKey, protocolLog);
    }

    private sealed class Session(
        FakeLoginServer server,
        ConnectionProfile profile,
        ConnectionCredentials credentials,
        HostKeyValidator validateHostKey,
        Action<string>? protocolLog) : IRemoteSession
    {
        public bool IsConnected { get; private set; }

        public RemoteCapabilities Capabilities => RemoteCapabilities.None;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (server.Delay is { } delay)
                await delay(profile, cancellationToken);
            lock (server.ProtocolsTried)
                server.ProtocolsTried.Add(profile.Protocol);
            if (server.ConnectFault is { } fault)
                throw new RemoteOperationException(fault);
            if (server.RefusesEncryption && profile.Protocol is ConnectionProtocol.FtpsExplicit or ConnectionProtocol.FtpsImplicit)
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.EncryptionNotSupported));
            if (server.RequiresEncryption && profile.Protocol == ConnectionProtocol.Ftp)
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.EncryptionRequired));
            if (!validateHostKey("ssh-ed25519", server.HostKeyFingerprint, 256))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.CertificateRejected));
            if (!string.IsNullOrEmpty(profile.PrivateKeyPath))
            {
                // 秘密鍵で入る（パスワードは見ない）
                if (server.KeyPassphrase is not null && credentials.Passphrase != server.KeyPassphrase)
                    throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.KeyPassphraseRequired));
            }
            else
            {
                lock (server.PasswordsTried)
                    server.PasswordsTried.Add(credentials.Password);
                if (credentials.Password != server.Password)
                    throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.AuthenticationFailed));
            }
            server.Logins++;
            IsConnected = true;
            protocolLog?.Invoke($"Response: 230 {profile.UserName} がログインしました");
        }

        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(server.Home);

        public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) =>
            server.Folders.TryGetValue(path, out var items)
                ? Task.FromResult<IReadOnlyList<RemoteItem>>(items)
                : throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound));

        public Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken)
        {
            var name = RemotePath.GetFileName(path);
            if (server.Files.TryGetValue(path, out var data))
                return Task.FromResult<RemoteItem?>(new RemoteItem(name, path, RemoteItemType.File, data.Length, DateTimeOffset.UtcNow));
            return Task.FromResult<RemoteItem?>(server.Folders.ContainsKey(path)
                ? new RemoteItem(name, path, RemoteItemType.Directory, 0, DateTimeOffset.UtcNow)
                : null);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            server.Operations.Enqueue("mkdir " + path);
            return Task.CompletedTask;
        }

        public async Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (server.BeforeUpload is { } before)
                await before(cancellationToken);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            server.Files[remotePath] = buffer.ToArray();
            progress?.Report(buffer.Length);
        }

        public async Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            var data = server.Files.TryGetValue(remotePath, out var bytes)
                ? bytes
                : throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound));
            await destination.WriteAsync(data.AsMemory((int)offset), cancellationToken);
            progress?.Report(data.Length - offset);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            server.Operations.Enqueue("rm " + path);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken)
        {
            server.Operations.Enqueue($"mv {fromPath} {toPath}");
            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            server.Operations.Enqueue("rmdir " + path);
            return Task.CompletedTask;
        }

        public Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken)
        {
            server.Operations.Enqueue($"chmod {Convert.ToString(mode, 8)} {path}");
            return Task.CompletedTask;
        }

        public Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task KeepAliveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>いつも失敗するパスワードの保管場所（資格情報マネージャーが使えないときのまね）。</summary>
internal sealed class BrokenSecretStore : ISecretStore
{
    public string? Read(Guid id) => throw new InvalidOperationException("保管場所を開けません");

    public void Write(Guid id, string secret) => throw new InvalidOperationException("保管場所を開けません");

    public void Delete(Guid id) => throw new InvalidOperationException("保管場所を開けません");
}

/// <summary>答えを決めておける、利用者への質問。</summary>
internal sealed class ScriptedPrompts : IConnectionPrompts
{
    public Queue<PasswordAnswer?> Passwords { get; } = new();

    public bool TrustHostKeys { get; set; } = true;

    public bool TrustCertificates { get; set; } = true;

    public int PasswordQuestions { get; private set; }

    public Queue<string?> Passphrases { get; } = new();

    /// <summary>パスフレーズを聞いたときの「前のが違った」の値。</summary>
    public List<bool> PassphraseQuestions { get; } = [];

    public List<HostKeyQuestion> HostKeyQuestions { get; } = [];

    public List<CertificateQuestion> CertificateQuestions { get; } = [];

    public Task<PasswordAnswer?> AskPasswordAsync(ConnectionProfile profile, bool previousAttemptFailed)
    {
        PasswordQuestions++;
        return Task.FromResult(Passwords.Count > 0 ? Passwords.Dequeue() : null);
    }

    public Task<string?> AskPassphraseAsync(ConnectionProfile profile, bool previousAttemptFailed)
    {
        PassphraseQuestions.Add(previousAttemptFailed);
        return Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);
    }

    public Task<bool> ConfirmHostKeyAsync(HostKeyQuestion question)
    {
        lock (HostKeyQuestions)
            HostKeyQuestions.Add(question);
        return Task.FromResult(TrustHostKeys);
    }

    public Task<bool> ConfirmCertificateAsync(CertificateQuestion question)
    {
        CertificateQuestions.Add(question);
        return Task.FromResult(TrustCertificates);
    }
}

/// <summary>答えを決めておける、接続先の作成・編集の画面。</summary>
internal sealed class ScriptedDialogs : IConnectionDialogs
{
    public Queue<ConnectionEditResult?> Edits { get; } = new();

    public Queue<ConnectionEditResult?> QuickConnects { get; } = new();

    public bool ConfirmDelete { get; set; } = true;

    public int DeleteQuestions { get; private set; }

    /// <summary>編集の画面を開いたときに、保存したパスワードがあると知らされたか。</summary>
    public List<bool> HasSavedPasswordShown { get; } = [];

    public Task<ConnectionEditResult?> EditAsync(ConnectionProfile? existing, bool hasSavedPassword)
    {
        HasSavedPasswordShown.Add(hasSavedPassword);
        return Task.FromResult(Edits.Count > 0 ? Edits.Dequeue() : null);
    }

    public Task<ConnectionEditResult?> AskQuickConnectAsync() =>
        Task.FromResult(QuickConnects.Count > 0 ? QuickConnects.Dequeue() : null);

    public Task<bool> ConfirmDeleteAsync(ConnectionProfile profile)
    {
        DeleteQuestions++;
        return Task.FromResult(ConfirmDelete);
    }
}

/// <summary>答えを決めておける、転送のときの質問。</summary>
internal sealed class ScriptedTransferDialogs : ITransferDialogs
{
    public ConflictPolicy? ConflictAnswer { get; set; } = ConflictPolicy.Overwrite;

    public bool StopAnswer { get; set; } = true;

    public List<(TransferDirection Direction, IReadOnlyList<string> Names, int Count)> ConflictQuestions { get; } = [];

    public List<(int Pending, bool ClosingApp)> StopQuestions { get; } = [];

    public Task<ConflictPolicy?> AskConflictAsync(TransferDirection direction, IReadOnlyList<string> names, int count)
    {
        ConflictQuestions.Add((direction, names, count));
        return Task.FromResult(ConflictAnswer);
    }

    public Task<bool> ConfirmStopTransfersAsync(int pendingCount, bool closingApp)
    {
        StopQuestions.Add((pendingCount, closingApp));
        return Task.FromResult(StopAnswer);
    }
}

/// <summary>比べ終わるのを待ってから、決めておいたとおりに選んで答える、同期の確認。</summary>
internal sealed class ScriptedSyncDialogs : ISyncDialogs
{
    public Action<SyncViewModel>? Choose { get; set; }

    public bool Answer { get; set; } = true;

    public List<SyncViewModel> Reviews { get; } = [];

    public async Task<bool> ReviewSyncAsync(SyncViewModel sync)
    {
        Reviews.Add(sync);
        await sync.Planning;
        Choose?.Invoke(sync);
        return Answer;
    }
}

/// <summary>答えを決めておける、控えから元に戻すときの質問。</summary>
internal sealed class ScriptedBackupDialogs : IBackupDialogs
{
    public bool Answer { get; set; } = true;

    public List<(IReadOnlyList<string> Names, int Count, IReadOnlyList<string> Changed)> Questions { get; } = [];

    public Task<bool> ConfirmRestoreAsync(IReadOnlyList<string> names, int count, IReadOnlyList<string> changed)
    {
        Questions.Add((names, count, changed));
        return Task.FromResult(Answer);
    }
}

/// <summary>答えを決めておける、ファイルの操作のときの質問。</summary>
internal sealed class ScriptedFileDialogs : IFileDialogs
{
    public Queue<string?> Names { get; } = new();

    public bool ConfirmDelete { get; set; } = true;

    public int? Permissions { get; set; }

    public List<(string Title, string Initial, string? ProblemForAnswer)> NameQuestions { get; } = [];

    public List<(IReadOnlyList<string> Names, bool Remote, bool Recoverable)> DeleteQuestions { get; } = [];

    public List<int?> PermissionQuestions { get; } = [];

    public Task<string?> AskNameAsync(string title, string initialName, string primaryButtonText, Func<string, string?> validate)
    {
        var answer = Names.Count > 0 ? Names.Dequeue() : null;
        NameQuestions.Add((title, initialName, answer is null ? null : validate(answer)));
        // 画面と同じく、問題のある名前では決定できない
        return Task.FromResult(answer is not null && validate(answer) is null ? answer : null);
    }

    public Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> names, bool remote, bool recoverable)
    {
        DeleteQuestions.Add((names, remote, recoverable));
        return Task.FromResult(ConfirmDelete);
    }

    public Task<int?> AskPermissionsAsync(IReadOnlyList<string> names, int? currentMode)
    {
        PermissionQuestions.Add(currentMode);
        return Task.FromResult(Permissions);
    }
}
