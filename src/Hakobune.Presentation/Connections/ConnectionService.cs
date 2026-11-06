using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hakobune.Core.Browsing;
using Hakobune.Core.Connections;
using Hakobune.Core.Remote;
using Hakobune.Core.Remote.Ftp;
using Hakobune.Core.Remote.Sftp;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Connections;

/// <summary>パスワードを聞いた答え。</summary>
/// <param name="Password">パスワード。</param>
/// <param name="Save">次から聞かないように保存するか。</param>
public sealed record PasswordAnswer(string Password, bool Save)
{
    /// <summary>パスワードがログなどに出ないようにする。</summary>
    public override string ToString() => $"{nameof(PasswordAnswer)} {{ Save = {Save} }}";
}

/// <summary>初めての（または鍵が変わった）SFTP サーバーの鍵を信頼してよいか、利用者に聞く内容。</summary>
public sealed record HostKeyQuestion(ConnectionProfile Profile, string Algorithm, string Fingerprint, int KeyLength, string? PreviousFingerprint)
{
    /// <summary>前に信頼した鍵と違うか（なりすましのおそれ）。</summary>
    public bool IsChanged => PreviousFingerprint is not null;
}

/// <summary>Windows が信頼しない FTPS の証明書を受け入れてよいか、利用者に聞く内容。</summary>
public sealed record CertificateQuestion(
    ConnectionProfile Profile,
    string Subject,
    string Issuer,
    string Fingerprint,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    IReadOnlyList<string> Problems,
    string? PreviousFingerprint)
{
    /// <summary>前に受け入れた証明書と違うか。</summary>
    public bool IsChanged => PreviousFingerprint is not null;
}

/// <summary>接続のときに利用者に聞くこと（画面がダイアログで聞く）。どのスレッドから呼ばれてもよいように作る。</summary>
public interface IConnectionPrompts
{
    /// <summary>パスワードを聞く。やめたら null。</summary>
    Task<PasswordAnswer?> AskPasswordAsync(ConnectionProfile profile, bool previousAttemptFailed);

    /// <summary>サーバーの鍵を信頼してよいか聞く。</summary>
    Task<bool> ConfirmHostKeyAsync(HostKeyQuestion question);

    /// <summary>証明書を受け入れてよいか聞く。</summary>
    Task<bool> ConfirmCertificateAsync(CertificateQuestion question);
}

/// <summary>パスワードの保管場所（Windows では資格情報マネージャー）。</summary>
public interface ISecretStore
{
    string? Read(Guid id);

    void Write(Guid id, string secret);

    void Delete(Guid id);
}

/// <summary>何も覚えない保管場所（試験や見本用）。</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<Guid, string> _secrets = [];
    private readonly Lock _gate = new();

    public string? Read(Guid id)
    {
        lock (_gate)
            return _secrets.GetValueOrDefault(id);
    }

    public void Write(Guid id, string secret)
    {
        lock (_gate)
            _secrets[id] = secret;
    }

    public void Delete(Guid id)
    {
        lock (_gate)
            _secrets.Remove(id);
    }
}

/// <summary>接続した結果。</summary>
public sealed class ConnectResult
{
    private ConnectResult(ConnectionProfile profile, IRemoteSessionFactory? factory, RemoteBrowser? browser, string? homeDirectory, TransferFailure? failure, bool cancelled)
    {
        Profile = profile;
        Factory = factory;
        Browser = browser;
        HomeDirectory = homeDirectory;
        Failure = failure;
        IsCancelled = cancelled;
    }

    public ConnectionProfile Profile { get; }

    /// <summary>転送用の接続を作るもの（画面用の接続とは別に作る）。</summary>
    public IRemoteSessionFactory? Factory { get; }

    /// <summary>画面用の接続。</summary>
    public RemoteBrowser? Browser { get; }

    /// <summary>ログインしたときのフォルダ。</summary>
    public string? HomeDirectory { get; }

    /// <summary>接続できなかった理由。</summary>
    public TransferFailure? Failure { get; }

    /// <summary>利用者がやめた（パスワードを入れなかった、鍵を信頼しなかった）。</summary>
    public bool IsCancelled { get; }

    public bool Succeeded => Browser is not null;

    /// <summary>画面用の接続を閉じる（エラーは無視する）。</summary>
    public async Task DisposeQuietlyAsync()
    {
        if (Browser is not { } browser)
            return;
        try
        {
            await browser.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 閉じるときのエラーは、利用者にできることがないので無視する
        }
    }

    internal static ConnectResult Connected(ConnectionProfile profile, IRemoteSessionFactory factory, RemoteBrowser browser, string home) =>
        new(profile, factory, browser, home, null, false);

    internal static ConnectResult Failed(ConnectionProfile profile, TransferFailure failure) => new(profile, null, null, null, failure, false);

    internal static ConnectResult Cancelled(ConnectionProfile profile) => new(profile, null, null, null, null, true);
}

/// <summary>接続先と秘密の情報から、接続を作るものを用意する（ふだんは <see cref="SessionFactories.Create"/>）。</summary>
public delegate IRemoteSessionFactory SessionFactoryBuilder(
    ConnectionProfile profile,
    ConnectionCredentials credentials,
    HostKeyValidator validateHostKey,
    CertificateValidator? validateCertificate);

/// <summary>
/// 保存した接続先に接続する。パスワードは保管場所から読み、無ければ聞く。初めてのサーバーの鍵や証明書は、利用者に確かめてから信頼する。
/// </summary>
public sealed class ConnectionService
{
    private const int MaxPasswordAttempts = 3;

    private readonly TrustStore _trust;
    private readonly ISecretStore _secrets;
    private readonly IConnectionPrompts _prompts;
    private readonly SessionFactoryBuilder _createFactory;

    // 転送の接続がいっせいに確かめに来ても、利用者に聞くのは 1 回にする
    private readonly SemaphoreSlim _askGate = new(1, 1);

    public ConnectionService(TrustStore trust, ISecretStore secrets, IConnectionPrompts prompts, SessionFactoryBuilder? createFactory = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(prompts);
        _trust = trust;
        _secrets = secrets;
        _prompts = prompts;
        _createFactory = createFactory ?? SessionFactories.Create;
    }

    /// <summary>
    /// 接続して、ログインできることを確かめる。<paramref name="password"/> を渡すと、保管場所より優先して使う（クイック接続など）。
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(ConnectionProfile profile, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var save = false;
        password ??= _secrets.TryRead(profile.Id);
        if (password is null && SessionFactories.NeedsPassword(profile))
        {
            var answer = await _prompts.AskPasswordAsync(profile, previousAttemptFailed: false).ConfigureAwait(false);
            if (answer is null)
                return ConnectResult.Cancelled(profile);
            (password, save) = (answer.Password, answer.Save);
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rejected = new RejectionFlag();
            var factory = _createFactory(
                profile,
                new ConnectionCredentials(password),
                CreateHostKeyValidator(profile, rejected),
                CreateCertificateValidator(profile, rejected));
            var browser = new RemoteBrowser(factory);
            string home;
            try
            {
                // 鍵や証明書を利用者に聞くあいだ待つことがあるので、画面のスレッドでは動かさない
                home = await Task.Run(() => browser.GetHomeDirectoryAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                if (rejected.Rejected)
                    return ConnectResult.Cancelled(profile);

                var failure = FailureClassifier.FromException(ex);
                if (failure.Reason != FailureReason.AuthenticationFailed || attempt >= MaxPasswordAttempts || !SessionFactories.NeedsPassword(profile))
                    return ConnectResult.Failed(profile, failure);

                // パスワードが違った。保存してあったものは消して、聞き直す
                _secrets.TryDelete(profile.Id);
                var answer = await _prompts.AskPasswordAsync(profile, previousAttemptFailed: true).ConfigureAwait(false);
                if (answer is null)
                    return ConnectResult.Cancelled(profile);
                (password, save) = (answer.Password, answer.Save);
                continue;
            }

            // 保存できなくても、今回の接続はそのまま使う（次に聞き直す）
            if (save && password is not null)
                _secrets.TryWrite(profile.Id, password);
            return ConnectResult.Connected(profile, factory, browser, home);
        }
    }

    /// <summary>SFTP のサーバーの鍵の確かめ方。信頼した鍵なら通し、そうでなければ利用者に聞く。</summary>
    internal HostKeyValidator CreateHostKeyValidator(ConnectionProfile profile, RejectionFlag rejected) => (algorithm, fingerprint, keyLength) =>
    {
        var host = profile.Host.Trim();
        var port = profile.EffectivePort;
        if (_trust.Check(host, port, algorithm, fingerprint).State == TrustState.Trusted)
            return true;

        _askGate.Wait();
        try
        {
            // 待っているあいだに、ほかの接続で信頼されたかもしれない
            var (state, previous) = _trust.Check(host, port, algorithm, fingerprint);
            if (state == TrustState.Trusted)
                return true;
            var accepted = _prompts.ConfirmHostKeyAsync(new HostKeyQuestion(profile, algorithm, fingerprint, keyLength, previous?.Fingerprint))
                .GetAwaiter().GetResult();
            if (accepted)
                TryTrust(host, port, algorithm, fingerprint);
            else
                rejected.Rejected = true;
            return accepted;
        }
        finally
        {
            _askGate.Release();
        }
    };

    /// <summary>
    /// FTPS の証明書の確かめ方。Windows が信頼する証明書はそのまま通す。自己署名などは、受け入れたことのある証明書なら通し、
    /// そうでなければ利用者に聞く。
    /// </summary>
    internal CertificateValidator CreateCertificateValidator(ConnectionProfile profile, RejectionFlag rejected) => (certificate, chain, errors) =>
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (certificate is null)
            return false;

        var host = profile.Host.Trim();
        var port = profile.EffectivePort;
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (_trust.Check(host, port, CertificateKind, fingerprint).State == TrustState.Trusted)
            return true;

        _askGate.Wait();
        try
        {
            var (state, previous) = _trust.Check(host, port, CertificateKind, fingerprint);
            if (state == TrustState.Trusted)
                return true;
            var certificate2 = certificate as X509Certificate2;
            var question = new CertificateQuestion(
                profile,
                certificate.Subject,
                certificate.Issuer,
                fingerprint,
                certificate2 is null ? null : new DateTimeOffset(certificate2.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                certificate2 is null ? null : new DateTimeOffset(certificate2.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                DescribeProblems(errors, chain),
                previous?.Fingerprint);
            var accepted = _prompts.ConfirmCertificateAsync(question).GetAwaiter().GetResult();
            if (accepted)
                TryTrust(host, port, CertificateKind, fingerprint);
            else
                rejected.Rejected = true;
            return accepted;
        }
        finally
        {
            _askGate.Release();
        }
    };

    // 信頼したことをファイルに書けなくても、今回は接続する（覚えてはいるので、アプリを閉じるまでは聞かない）
    private void TryTrust(string host, int port, string kind, string fingerprint)
    {
        try
        {
            _trust.Trust(host, port, kind, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>証明書の指紋を覚えるときの種類。</summary>
    public const string CertificateKind = "x509";

    /// <summary>証明書の問題を、利用者向けの文にする。</summary>
    internal static IReadOnlyList<string> DescribeProblems(SslPolicyErrors errors, X509Chain? chain)
    {
        var problems = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            problems.Add("証明書が送られてきませんでした");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            problems.Add("証明書の名前が、接続先の名前と一致しません");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var statuses = chain?.ChainStatus.Select(status => status.Status).ToList() ?? [];
            if (statuses.Contains(X509ChainStatusFlags.NotTimeValid))
                problems.Add("証明書の有効期限が切れています");
            if (statuses.Contains(X509ChainStatusFlags.UntrustedRoot) || statuses.Contains(X509ChainStatusFlags.PartialChain) || statuses.Count == 0)
                problems.Add("信頼できる発行元の証明書ではありません（自己署名など）");
            if (statuses.Contains(X509ChainStatusFlags.Revoked))
                problems.Add("証明書は取り消されています");
        }
        return problems;
    }
}

/// <summary>鍵や証明書を利用者が断ったことを、確かめ方の中から伝える。</summary>
internal sealed class RejectionFlag
{
    public bool Rejected { get; set; }
}
