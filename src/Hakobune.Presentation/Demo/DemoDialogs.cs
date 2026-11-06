using Hakobune.Core.Connections;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Backups;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Transfers;

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

    /// <summary>名前の変更（サーバー側）。</summary>
    Rename,

    /// <summary>サーバー側のファイルの削除の確認。</summary>
    DeleteFiles,

    /// <summary>属性の変更。</summary>
    Permissions,

    /// <summary>送り先に同じ名前があるときの扱い。</summary>
    Conflict,

    /// <summary>転送中に閉じるときの確認。</summary>
    StopTransfers,

    /// <summary>設定。</summary>
    Settings,

    /// <summary>設定の「送らないファイル」のところ。</summary>
    SettingsExclusions,

    /// <summary>同期の確認（比べ終わったところ）。</summary>
    Sync,

    /// <summary>同期の確認で、サーバーのほうが新しいものも送り、サーバーにだけあるものを消すと選んだところ。</summary>
    SyncDelete,

    /// <summary>控えから元に戻すときの確認（1 件は、控えを取ったあとにも変わっている）。</summary>
    Restore,
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

    /// <summary>見本の設定の保存先。</summary>
    public const string DataDirectory = @"C:\Users\user\AppData\Roaming\Hakobune";

    /// <summary>同期の確認に出す、比べ終わった結果（見本の画面のパソコンのフォルダと、サーバーのフォルダ）。</summary>
    public static SyncPlan SyncPlan { get; } = CreateSyncPlan();

    /// <summary>見本の同期の確認を作る（すぐに比べ終わる）。</summary>
    public static SyncViewModel CreateSync()
    {
        var plan = SyncPlan;
        var sync = new SyncViewModel(SyncDirection.Upload, plan.SourceRoot, $"{Profile.Host} {plan.TargetRoot}", (_, _) => Task.FromResult(plan))
        {
            BacksUp = true,
        };
        sync.Start();
        return sync;
    }

    private static SyncPlan CreateSyncPlan()
    {
        const string local = @"C:\Users\user\Sites\shop";
        const string remote = "/var/www/html";
        static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
            new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));
        static string Name(string relative) => relative[(relative.LastIndexOf('/') + 1)..];
        static FileEntry Local(string relative, long size, DateTimeOffset modified) =>
            new(Name(relative), local + "\\" + relative.Replace('/', '\\'), FileEntryType.File, size, modified);
        static FileEntry Remote(string relative, long size, DateTimeOffset modified) =>
            new(Name(relative), remote + "/" + relative, FileEntryType.File, size, modified, "-rw-r--r--");

        SyncItem[] items =
        [
            new(SyncChange.OnlyOnTarget, "campaign-2025", null,
                new FileEntry("campaign-2025", remote + "/campaign-2025", FileEntryType.Directory, 0, At(2025, 12, 1, 9, 30), "drwxr-xr-x")),
            new(SyncChange.TargetNewer, "contact.html", Local("contact.html", 6_120, At(2026, 11, 2, 14, 5)), Remote("contact.html", 6_388, At(2026, 11, 5, 9, 41))),
            new(SyncChange.Changed, "css/site.css", Local("css/site.css", 18_204, At(2026, 11, 6, 11, 58)), Remote("css/site.css", 17_950, At(2026, 10, 28, 16, 20))),
            new(SyncChange.New, "downloads", new FileEntry("downloads", local + "\\downloads", FileEntryType.Directory, 0, At(2026, 11, 6, 10, 2)), null),
            new(SyncChange.OnlyOnTarget, "images/old-banner.png", null, Remote("images/old-banner.png", 88_412, At(2025, 7, 3, 18, 12))),
            new(SyncChange.New, "images/商品_12.jpg", Local("images/商品_12.jpg", 248_310, At(2026, 11, 6, 11, 40)), null),
            new(SyncChange.New, "images/商品_13.jpg", Local("images/商品_13.jpg", 231_877, At(2026, 11, 6, 11, 41)), null),
            new(SyncChange.Changed, "index.html", Local("index.html", 12_840, At(2026, 11, 6, 12, 10)), Remote("index.html", 12_402, At(2026, 11, 1, 18, 30))),
            new(SyncChange.New, "news/2026-11.html", Local("news/2026-11.html", 8_402, At(2026, 11, 6, 12, 2)), null),
        ];
        return new SyncPlan(
            SyncDirection.Upload, local, remote, items, SameCount: 128, ExcludedCount: 3, UnreadableFolders: [], TooMany: false, ClockOffset: TimeSpan.Zero);
    }

    /// <summary>
    /// 見本のダイアログを出す。閉じるまで待つ。ファイル・転送・設定のダイアログは、その画面と <paramref name="main"/> があるときだけ出す。
    /// </summary>
    public static async Task ShowAsync(
        DemoDialog dialog,
        IConnectionPrompts prompts,
        IConnectionDialogs dialogs,
        IFileDialogs? fileDialogs = null,
        ITransferDialogs? transferDialogs = null,
        ISettingsDialogs? settingsDialogs = null,
        MainViewModel? main = null,
        ISyncDialogs? syncDialogs = null,
        IBackupDialogs? backupDialogs = null)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(dialogs);
        switch (dialog)
        {
            case DemoDialog.Rename when fileDialogs is not null:
                await fileDialogs.AskNameAsync("名前の変更", "about.html", "変更", name => FileNames.Validate(name, PaneKind.Remote)).ConfigureAwait(true);
                break;
            case DemoDialog.DeleteFiles when fileDialogs is not null:
                await fileDialogs.ConfirmDeleteAsync(["index.html", "about.html", "images"], remote: true, recoverable: true).ConfigureAwait(true);
                break;
            case DemoDialog.Permissions when fileDialogs is not null:
                await fileDialogs.AskPermissionsAsync(["index.html"], Convert.ToInt32("644", 8)).ConfigureAwait(true);
                break;
            case DemoDialog.Conflict when transferDialogs is not null:
                await transferDialogs.AskConflictAsync(
                    TransferDirection.Upload,
                    ["index.html", "about.html", "css/site.css", "images/logo.png", "images/商品_01.jpg"],
                    count: 12).ConfigureAwait(true);
                break;
            case DemoDialog.StopTransfers when transferDialogs is not null:
                await transferDialogs.ConfirmStopTransfersAsync(118, closingApp: true).ConfigureAwait(true);
                break;
            case DemoDialog.Settings when settingsDialogs is not null && main is not null:
                await settingsDialogs.ShowSettingsAsync(new SettingsViewModel(main, DataDirectory)).ConfigureAwait(true);
                break;
            case DemoDialog.SettingsExclusions when settingsDialogs is not null && main is not null:
                await settingsDialogs.ShowSettingsAsync(new SettingsViewModel(main, DataDirectory) { OpensAtExclusions = true }).ConfigureAwait(true);
                break;
            case DemoDialog.Sync when syncDialogs is not null:
                await syncDialogs.ReviewSyncAsync(CreateSync()).ConfigureAwait(true);
                break;
            case DemoDialog.SyncDelete when syncDialogs is not null:
                var sync = CreateSync();
                sync.IncludeTargetNewer = true;
                sync.DeleteOnlyOnTarget = true;
                await syncDialogs.ReviewSyncAsync(sync).ConfigureAwait(true);
                break;
            case DemoDialog.Restore when backupDialogs is not null:
                await backupDialogs.ConfirmRestoreAsync(
                    ["/var/www/html/index.html", "/var/www/html/css/site.css", "/var/www/html/campaign-2025/index.html"],
                    5,
                    ["/var/www/html/index.html"]).ConfigureAwait(true);
                break;
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
