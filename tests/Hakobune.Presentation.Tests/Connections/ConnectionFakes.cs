using Hakobune.Core.Connections;
using Hakobune.Core.Remote;
using Hakobune.Core.Remote.Ftp;
using Hakobune.Core.Remote.Sftp;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;

namespace Hakobune.Presentation.Tests.Connections;

/// <summary>パスワードとサーバーの鍵を確かめる、小さな偽のサーバー。</summary>
internal sealed class FakeLoginServer
{
    public string Password { get; set; } = "right";

    public string HostKeyFingerprint { get; set; } = "AAAA";

    public string Home { get; set; } = "/home/web";

    public Dictionary<string, List<RemoteItem>> Folders { get; } = new(StringComparer.Ordinal);

    public TransferFailure? ConnectFault { get; set; }

    /// <summary>接続の前に待つ（遅いサーバーのまね）。</summary>
    public Func<ConnectionProfile, CancellationToken, Task>? Delay { get; set; }

    /// <summary>秘密鍵で入るときの、鍵のパスフレーズ（null なら掛かっていない）。</summary>
    public string? KeyPassphrase { get; set; }

    public int Logins { get; private set; }

    public List<string?> PasswordsTried { get; } = [];

    public SessionFactoryBuilder Builder => (profile, credentials, validateHostKey, validateCertificate) =>
        new Factory(this, profile, credentials, validateHostKey);

    private sealed class Factory(FakeLoginServer server, ConnectionProfile profile, ConnectionCredentials credentials, HostKeyValidator validateHostKey) : IRemoteSessionFactory
    {
        public IRemoteSession Create() => new Session(server, profile, credentials, validateHostKey);
    }

    private sealed class Session(FakeLoginServer server, ConnectionProfile profile, ConnectionCredentials credentials, HostKeyValidator validateHostKey) : IRemoteSession
    {
        public bool IsConnected { get; private set; }

        public RemoteCapabilities Capabilities => RemoteCapabilities.None;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (server.Delay is { } delay)
                await delay(profile, cancellationToken);
            if (server.ConnectFault is { } fault)
                throw new RemoteOperationException(fault);
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
        }

        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(server.Home);

        public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) =>
            server.Folders.TryGetValue(path, out var items)
                ? Task.FromResult<IReadOnlyList<RemoteItem>>(items)
                : throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound));

        public Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken) => Task.FromResult<RemoteItem?>(null);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken) => Task.CompletedTask;

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
