using Hakobune.Core.Connections;
using Hakobune.Presentation.Connections;

namespace Hakobune.Presentation.Demo;

/// <summary>見本のダイアログ（<c>--demo --dialog</c>）。</summary>
public enum DemoDialog
{
    /// <summary>新しい接続。</summary>
    NewConnection,

    /// <summary>接続先の編集（詳しい設定を開いた状態）。</summary>
    EditConnection,

    /// <summary>クイック接続。</summary>
    QuickConnect,

    /// <summary>秘密鍵のパスフレーズを聞く。</summary>
    Passphrase,

    /// <summary>パスワードを聞く（前回のパスワードが違った）。</summary>
    Password,

    /// <summary>初めての SFTP サーバーの鍵。</summary>
    HostKey,

    /// <summary>前と違う SFTP サーバーの鍵。</summary>
    ChangedHostKey,

    /// <summary>Windows が信頼しない FTPS の証明書。</summary>
    Certificate,

    /// <summary>接続先の削除の確認。</summary>
    Delete,
}

/// <summary>見本のダイアログに出す中身。</summary>
public static class DemoDialogs
{
    /// <summary>見本の接続先（見本の画面の「ショップ（本番）」）。</summary>
    public static ConnectionProfile Profile { get; } = new()
    {
        Name = "ショップ（本番）",
        Group = "仕事",
        Protocol = ConnectionProtocol.Sftp,
        Host = "example.com",
        UserName = "web",
        RemoteDirectory = "/var/www/html",
        LocalDirectory = @"C:\Users\user\Sites\shop",
    };

    /// <summary>見本の FTPS の接続先。</summary>
    public static ConnectionProfile FtpsProfile { get; } = new()
    {
        Name = "社内ポータル",
        Group = "仕事",
        Protocol = ConnectionProtocol.FtpsExplicit,
        Host = "portal.example.com",
        UserName = "staff",
    };

    /// <summary>初めてのサーバーの鍵。</summary>
    public static HostKeyQuestion HostKey { get; } =
        new(Profile, "ssh-ed25519", "MlA7TvxYkG/crSx09bxWS/lJ35viEPETejtmMBgPwMs", 256, PreviousFingerprint: null);

    /// <summary>前と違うサーバーの鍵。</summary>
    public static HostKeyQuestion ChangedHostKey { get; } =
        new(Profile, "ssh-ed25519", "SUcEhMieDvrI8ssNxfA+Aq97neVAC3m+A/48SlvvVhs", 256, "MlA7TvxYkG/crSx09bxWS/lJ35viEPETejtmMBgPwMs");

    /// <summary>自己署名の証明書。</summary>
    public static CertificateQuestion Certificate { get; } = new(
        FtpsProfile,
        "CN=portal.example.com",
        "CN=portal.example.com",
        "101B19C7EA72722C40420C7E5DEA1063C6971AF7CDE9F0E235DCEBE9071A862C",
        new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.FromHours(9)),
        new DateTimeOffset(2027, 3, 31, 12, 0, 0, TimeSpan.FromHours(9)),
        ConnectionService.DescribeProblems(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, null),
        PreviousFingerprint: null);

    /// <summary>見本のダイアログを出す。閉じるまで待つ。</summary>
    public static async Task ShowAsync(DemoDialog dialog, IConnectionPrompts prompts, IConnectionDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(dialogs);
        switch (dialog)
        {
            case DemoDialog.NewConnection:
                await dialogs.EditAsync(null, hasSavedPassword: false).ConfigureAwait(true);
                break;
            case DemoDialog.EditConnection:
                await dialogs.EditAsync(Profile, hasSavedPassword: true).ConfigureAwait(true);
                break;
            case DemoDialog.QuickConnect:
                await dialogs.AskQuickConnectAsync().ConfigureAwait(true);
                break;
            case DemoDialog.Passphrase:
                await prompts.AskPassphraseAsync(Profile with { PrivateKeyPath = @"C:\Users\user\.ssh\id_ed25519" }, previousAttemptFailed: false).ConfigureAwait(true);
                break;
            case DemoDialog.Password:
                await prompts.AskPasswordAsync(Profile, previousAttemptFailed: true).ConfigureAwait(true);
                break;
            case DemoDialog.HostKey:
                await prompts.ConfirmHostKeyAsync(HostKey).ConfigureAwait(true);
                break;
            case DemoDialog.ChangedHostKey:
                await prompts.ConfirmHostKeyAsync(ChangedHostKey).ConfigureAwait(true);
                break;
            case DemoDialog.Certificate:
                await prompts.ConfirmCertificateAsync(Certificate).ConfigureAwait(true);
                break;
            case DemoDialog.Delete:
                await dialogs.ConfirmDeleteAsync(Profile).ConfigureAwait(true);
                break;
        }
    }
}
