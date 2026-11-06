using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Hakobune.Core.Remote.Ftp;

/// <summary>FTP の暗号化のしかた。</summary>
public enum FtpSecurity
{
    /// <summary>暗号化しない（FTP）。</summary>
    None,

    /// <summary>接続したあとで暗号化する（FTPS 明示的、FTPES）。</summary>
    Explicit,

    /// <summary>最初から暗号化する（FTPS 暗黙的。ふつうはポート 990）。</summary>
    Implicit,
}

/// <summary>サーバーの証明書を確かめる。受け入れるなら true を返す。</summary>
public delegate bool CertificateValidator(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors);

/// <summary>FTP / FTPS の接続先の設定。</summary>
public sealed record FtpConnectionSettings
{
    /// <summary>ホスト名または IP アドレス。</summary>
    public required string Host { get; init; }

    /// <summary>ポート番号。0 なら既定（FTP・FTPES は 21、暗黙的 FTPS は 990）。</summary>
    public int Port { get; init; }

    public required string UserName { get; init; }

    public required string Password { get; init; }

    public FtpSecurity Security { get; init; } = FtpSecurity.Explicit;

    /// <summary>
    /// 証明書の確かめ方。null なら Windows が信頼する証明書だけを受け入れる。
    /// </summary>
    public CertificateValidator? ValidateCertificate { get; init; }

    /// <summary>ファイル名の文字コード。null なら UTF-8（古いサーバー向けに Shift_JIS なども選べる）。</summary>
    public Encoding? FileNameEncoding { get; init; }

    /// <summary>パッシブモードを使う（ほとんどの環境ではこちら）。</summary>
    public bool UsePassiveMode { get; init; } = true;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 通信の詳細（送ったコマンドとサーバーの返事）を書く先。null なら書かない。パスワードは「***」にして書く。
    /// 転送の接続からも呼ばれるので、どのスレッドから呼ばれてもよいものにする。
    /// </summary>
    public Action<string>? ProtocolLog { get; init; }

    // パスワードがログなどに出ないようにする。
    public override string ToString() =>
        $"{nameof(FtpConnectionSettings)} {{ Host = {Host}, Port = {Port}, UserName = {UserName}, Security = {Security} }}";
}
