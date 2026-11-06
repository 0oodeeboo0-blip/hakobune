using System.Text;
using Hakobune.Core.Remote;
using Hakobune.Core.Remote.Ftp;
using Hakobune.Core.Remote.Sftp;

namespace Hakobune.Core.Connections;

/// <summary>接続に使う秘密の情報（保存した接続先とは別に持つ）。</summary>
/// <param name="Password">パスワード。秘密鍵だけで入るときは null。</param>
/// <param name="Passphrase">秘密鍵のパスフレーズ。</param>
public sealed record ConnectionCredentials(string? Password, string? Passphrase = null)
{
    /// <summary>パスワードなどがログに出ないようにする。</summary>
    public override string ToString() => nameof(ConnectionCredentials);
}

/// <summary>保存した接続先から、接続を作るものを用意する。</summary>
public static class SessionFactories
{
    static SessionFactories()
    {
        // Shift_JIS や EUC-JP を使えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 接続を作るものを用意する。サーバーの鍵（SFTP）と証明書（FTPS）は、渡した確かめ方で確かめる。
    /// </summary>
    public static IRemoteSessionFactory Create(
        ConnectionProfile profile,
        ConnectionCredentials credentials,
        HostKeyValidator validateHostKey,
        CertificateValidator? validateCertificate)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(validateHostKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Host);

        var encoding = GetEncoding(profile.FileNameEncoding);
        if (profile.Protocol == ConnectionProtocol.Sftp)
        {
            return new SftpSessionFactory(new SftpConnectionSettings
            {
                Host = profile.Host.Trim(),
                Port = profile.EffectivePort,
                UserName = profile.UserName,
                Password = credentials.Password,
                PrivateKeyPath = string.IsNullOrWhiteSpace(profile.PrivateKeyPath) ? null : profile.PrivateKeyPath,
                PrivateKeyPassphrase = credentials.Passphrase,
                ValidateHostKey = validateHostKey,
                FileNameEncoding = encoding,
            });
        }

        // ユーザー名が空なら匿名 FTP として入る
        var anonymous = string.IsNullOrEmpty(profile.UserName);
        return new FtpSessionFactory(new FtpConnectionSettings
        {
            Host = profile.Host.Trim(),
            Port = profile.Port,
            UserName = anonymous ? "anonymous" : profile.UserName,
            Password = credentials.Password ?? (anonymous ? "anonymous@" : ""),
            Security = profile.Protocol switch
            {
                ConnectionProtocol.FtpsExplicit => FtpSecurity.Explicit,
                ConnectionProtocol.FtpsImplicit => FtpSecurity.Implicit,
                _ => FtpSecurity.None,
            },
            ValidateCertificate = validateCertificate,
            FileNameEncoding = encoding,
            UsePassiveMode = profile.UsePassiveMode,
        });
    }

    /// <summary>
    /// 文字コードの名前から文字コードを得る。UTF-8 なら null（既定のまま）。知らない名前も null にする。
    /// </summary>
    public static Encoding? GetEncoding(string? name)
    {
        var key = name?.Trim().ToLowerInvariant() ?? "";
        switch (key)
        {
            case "" or "utf-8" or "utf8":
                return null;
            case "shift_jis" or "shift-jis" or "sjis" or "cp932" or "windows-31j":
                return Encoding.GetEncoding(932);
            case "euc-jp" or "eucjp":
                return Encoding.GetEncoding("euc-jp");
            default:
                try
                {
                    return Encoding.GetEncoding(key);
                }
                catch (ArgumentException)
                {
                    return null;
                }
        }
    }

    /// <summary>接続に秘密の情報（パスワード）が要るか。秘密鍵で入る SFTP と、匿名 FTP は要らない。</summary>
    public static bool NeedsPassword(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Protocol == ConnectionProtocol.Sftp)
            return string.IsNullOrWhiteSpace(profile.PrivateKeyPath);
        return !string.IsNullOrEmpty(profile.UserName);
    }
}
