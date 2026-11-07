using Hakobune.Core.Backups;
using Hakobune.Core.Connections;
using Hakobune.Core.Logging;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Transfers;
using Hakobune.Presentation.Updates;

namespace Hakobune.Presentation.Demo;

/// <summary>見本の画面のどの場面か。</summary>
public enum DemoScenario
{
    /// <summary>500 件のアップロードの途中。</summary>
    Normal,

    /// <summary>そのうえで、サーバーの一覧の更新に失敗し、前回の一覧を出している。</summary>
    Stale,

    /// <summary>
    /// アップロードのあと、サーバーの一覧を取り直したところ（増えた 2 つと変わった index.html が光る）。
    /// 取り直しは画面が出てから <see cref="DemoData.ShowUploadedAsync"/> で行う。
    /// </summary>
    Refreshed,
}

/// <summary>
/// 見本の画面（<c>--demo</c>）。デザイン案と同じ中身を、本物の ViewModel に流し込む。画面写真での見た目の確認に使う。
/// </summary>
public static class DemoData
{
    private const string LocalFolder = @"C:\Users\user\Sites\shop";
    private const string RemoteFolder = "/var/www/html";
    private const string PhotoFolder = "/var/www/html/商品写真";
    private const string LocalPhotoFolder = @"C:\Users\user\Sites\shop\商品写真";
    private const string Directory755 = "drwxr-xr-x";
    private const string File644 = "-rw-r--r--";

    /// <summary>見本の時刻（2026/11/06 12:36:20、パソコンの時刻帯）。</summary>
    public static DateTimeOffset Now { get; } = At(2026, 11, 6, 12, 36, 20);

    /// <summary>見本の接続先（ショップ（本番））の ID。控えの見本に使う。</summary>
    public static Guid ConnectionId { get; } = new("6d2f4c1e-3b7a-4f0e-9c55-2a1b8e7d9f01");

    /// <summary>見本の画面で、新しい版の知らせ（ステータスバー）を出す。</summary>
    public static void ShowSampleUpdate(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.AvailableUpdate = new UpdateInfo(new Version(0, 3, 1), "Hakobune 0.3.1", new Uri(UpdateCheck.ReleasesPage, "releases/tag/v0.3.1"));
    }

    /// <summary>見本の通知に使う、転送のひと区切り（12 件・3.3 MB を送り終えた）。</summary>
    public static TransferRunEnded SampleRunEnded { get; } =
        new(new TransferTotals(Uploaded: 12, Downloaded: 0, Deleted: 0, Folders: 1, Failed: 0, UploadedBytes: 3_456_789, DownloadedBytes: 0), Stopped: false);

    /// <summary>見本の画面を作る。</summary>
    public static async Task<MainViewModel> CreateAsync(DemoScenario scenario, SynchronizationContext? context = null)
    {
        var time = new FixedTimeProvider(Now);

        var local = new FilePaneViewModel(PaneKind.Local, time);
        var localSource = new DemoFileSource(LocalFolder, windowsPaths: true, time);
        localSource.AddFolder(LocalFolder, LocalEntries(localSource));
        // 送らないファイル（Thumbs.db）には印が付く
        local.Exclusions = new ExclusionSettings().ToRules();
        await local.SetSourceAsync(localSource).ConfigureAwait(true);
        local.Focus("商品写真");

        var remote = new FilePaneViewModel(PaneKind.Remote, time);
        var remoteSource = new DemoFileSource(RemoteFolder, windowsPaths: false, time, rootLabel: "example.com");
        remoteSource.AddFolder(RemoteFolder, RemoteEntries(remoteSource));
        await remote.SetSourceAsync(remoteSource).ConfigureAwait(true);
        if (scenario == DemoScenario.Stale)
        {
            remoteSource.Failure = TransferFailure.Transient(FailureReason.ConnectionLost);
            await remote.RefreshAsync().ConfigureAwait(true);
        }

        var (items, rates) = TransferItems();
        var transfers = new TransferListViewModel(
            new DemoTransferSource(items, RetryPolicy.Default.MaxAttempts, maxConcurrency: 3), time, context);
        foreach (var (id, bytesPerSecond) in rates)
            transfers.SetRate(id, bytesPerSecond);

        var connections = new ConnectionListViewModel();
        var shop = new ConnectionItemViewModel("ショップ（本番）", ConnectionProtocol.Sftp, "example.com") { IsConnected = true };
        connections.Add(new ConnectionGroupViewModel("仕事",
        [
            shop,
            new ConnectionItemViewModel("ショップ（検証）", ConnectionProtocol.Sftp, "staging.example.com"),
            new ConnectionItemViewModel("社内ポータル", ConnectionProtocol.FtpsExplicit, "portal.example.com"),
        ]));
        connections.Add(new ConnectionGroupViewModel("個人",
        [
            new ConnectionItemViewModel("ブログ", ConnectionProtocol.Ftp, "blog.example.com"),
            new ConnectionItemViewModel("写真の保管", ConnectionProtocol.Sftp, "photos.example.com"),
        ]));
        connections.Select(shop);

        var status = new ConnectionStatusViewModel();
        status.ShowConnected(shop);

        var main = new MainViewModel(local, remote, transfers, connections, status, log: SampleLog(), backups: await SampleBackupsAsync().ConfigureAwait(true));
        main.BackupList.SetConnection(ConnectionId);
        return main;
    }

    // 下の「控え」の見本（前の日のアップロード、パネルでの削除、同期で上書き・削除したもの）。控えは一時フォルダに置く
    private static async Task<BackupStore> SampleBackupsAsync()
    {
        var parent = Path.Combine(Path.GetTempPath(), "hakobune-demo-backups");
        CleanOldDemoFolders(parent);
        var clock = new SettableTimeProvider { Now = Now };
        var store = new BackupStore(Path.Combine(parent, Guid.NewGuid().ToString("N")), new BackupLimits(), clock);

        async Task TakeAsync(Guid batch, BackupKind kind, string path, int size, DateTimeOffset modified) =>
            await store.SaveAsync(
                ConnectionId,
                batch,
                kind,
                RemoteFolder + "/" + path,
                size,
                modified,
                null,
                (stream, token) => stream.WriteAsync(new byte[size], token).AsTask()).ConfigureAwait(false);

        clock.Now = At(2026, 11, 5, 17, 20);
        var upload = store.BeginBatch(ConnectionId, "アップロード");
        await TakeAsync(upload, BackupKind.ServerOverwritten, "about.html", 9_100, At(2026, 10, 30, 11, 5)).ConfigureAwait(true);

        clock.Now = At(2026, 11, 6, 11, 52);
        var delete = store.BeginBatch(ConnectionId, "削除");
        await TakeAsync(delete, BackupKind.ServerDeleted, "test.php", 1_240, At(2026, 9, 12, 10, 2)).ConfigureAwait(true);

        clock.Now = At(2026, 11, 6, 12, 30);
        var sync = store.BeginBatch(ConnectionId, "同期（パソコン → サーバー）");
        await TakeAsync(sync, BackupKind.ServerOverwritten, "index.html", 12_402, At(2026, 11, 1, 18, 30)).ConfigureAwait(true);
        await TakeAsync(sync, BackupKind.ServerOverwritten, "css/site.css", 17_950, At(2026, 10, 28, 16, 20)).ConfigureAwait(true);
        await TakeAsync(sync, BackupKind.ServerDeleted, "campaign-2025/index.html", 8_200, At(2025, 12, 1, 9, 30)).ConfigureAwait(true);
        await TakeAsync(sync, BackupKind.ServerDeleted, "campaign-2025/banner.jpg", 245_760, At(2025, 12, 1, 9, 28)).ConfigureAwait(true);
        await TakeAsync(sync, BackupKind.ServerDeleted, "images/old-banner.png", 88_412, At(2025, 7, 3, 18, 12)).ConfigureAwait(true);
        return store;
    }

    // 前に見本を出したときの控え（1 時間より前のもの）を片づける
    private static void CleanOldDemoFolders(string parent)
    {
        try
        {
            if (!Directory.Exists(parent))
                return;
            foreach (var folder in Directory.EnumerateDirectories(parent))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > TimeSpan.FromHours(1))
                    Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 見本の「アップロードのあと」: サーバー側に banner.jpg と campaign.html が増え、index.html が変わったことにして、
    /// 一覧を取り直す（増えた・変わった行だけが、しばらく光る）。
    /// </summary>
    public static Task ShowUploadedAsync(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (viewModel.Remote.Source is not DemoFileSource source)
            return Task.CompletedTask;
        var entries = RemoteEntries(source);
        entries[entries.FindIndex(entry => entry.Name == "index.html")] =
            FileEntryAt(source, RemoteFolder, "index.html", 19_100, At(2026, 11, 6, 12, 36), File644);
        entries.Add(FileEntryAt(source, RemoteFolder, "banner.jpg", 253_952, At(2026, 11, 6, 12, 36), File644));
        entries.Add(FileEntryAt(source, RemoteFolder, "campaign.html", 6_200, At(2026, 11, 6, 12, 36), File644));
        source.AddFolder(RemoteFolder, entries);
        return viewModel.Remote.RefreshAsync();
    }

    // 下の「ログ」の見本（接続して 500 件を送っている途中）
    private static ActivityLog SampleLog()
    {
        var clock = new SettableTimeProvider();
        var log = new ActivityLog(timeProvider: clock);
        const string shop = "ショップ（本番）";
        void Write(int minute, int second, LogKind kind, string message)
        {
            clock.Now = At(2026, 11, 6, 12, minute, second);
            log.Add(kind, message, shop);
        }

        Write(31, 58, LogKind.Info, "ショップ（本番） に接続しています（SFTP・example.com:22）");
        Write(31, 58, LogKind.Detail, "Status:   example.com:22 に web で接続します");
        Write(31, 59, LogKind.Detail, "Status:   SSH-2.0-OpenSSH_9.6p1 に接続しました（鍵交換 curve25519-sha256、暗号 aes128-gcm@openssh.com、サーバーの鍵 ssh-ed25519）");
        Write(31, 59, LogKind.Success, "接続しました（SFTP・暗号化）");
        Write(32, 4, LogKind.Info, "アップロードに 500 件を加えました（1.2 GB）");
        Write(32, 6, LogKind.Detail, "Command:  put /var/www/html/商品写真/coat-black-01.jpg");
        Write(32, 7, LogKind.Success, "送りました: /var/www/html/商品写真/coat-black-01.jpg（412 KB）");
        Write(35, 41, LogKind.Error, "送れませんでした: /var/www/html/admin/config.php: 権限がありません（550）");
        Write(36, 2, LogKind.Error, "送れませんでした: /var/www/html/video/紹介動画.mp4: サーバーの容量が足りません（552）");
        Write(36, 9, LogKind.Warning, "やり直します（2/5 回目の失敗）: /var/www/html/商品写真/scarf-red-05.jpg: 接続が切れました");
        Write(36, 18, LogKind.Success, "送りました: /var/www/html/商品写真/winter-coat-03.jpg（388 KB）");
        Write(36, 19, LogKind.Success, "送りました: /var/www/html/商品写真/winter-coat-04.jpg（405 KB）");
        return log;
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));

    private static FileEntry FolderEntry(DemoFileSource source, string parent, string name, DateTimeOffset modified, string? permissions = null) =>
        new(name, source.Combine(parent, name), FileEntryType.Directory, 0, modified, permissions);

    private static FileEntry FileEntryAt(DemoFileSource source, string parent, string name, long size, DateTimeOffset modified, string? permissions = null) =>
        new(name, source.Combine(parent, name), FileEntryType.File, size, modified, permissions);

    private static List<FileEntry> LocalEntries(DemoFileSource source) =>
    [
        FolderEntry(source, LocalFolder, "css", At(2026, 11, 5, 18, 22)),
        FolderEntry(source, LocalFolder, "images", At(2026, 11, 6, 12, 31)),
        FolderEntry(source, LocalFolder, "js", At(2026, 10, 28, 9, 14)),
        FolderEntry(source, LocalFolder, "商品写真", At(2026, 11, 6, 12, 35)),
        FileEntryAt(source, LocalFolder, ".htaccess", 1_536, At(2026, 8, 12, 14, 20)),
        FileEntryAt(source, LocalFolder, "about.html", 9_100, At(2026, 11, 2, 16, 5)),
        FileEntryAt(source, LocalFolder, "backup-2026-11.zip", 134_637_158, At(2026, 11, 1, 3, 0)),
        FileEntryAt(source, LocalFolder, "index.html", 18_300, At(2026, 11, 6, 12, 30)),
        FileEntryAt(source, LocalFolder, "logo.png", 46_800, At(2026, 10, 20, 10, 12)),
        FileEntryAt(source, LocalFolder, "robots.txt", 120, At(2026, 9, 30, 8, 0)),
        FileEntryAt(source, LocalFolder, "sitemap.xml", 3_900, At(2026, 11, 6, 12, 30)),
        FileEntryAt(source, LocalFolder, "Thumbs.db", 18_432, At(2026, 11, 6, 12, 35)),
        FileEntryAt(source, LocalFolder, "お問い合わせ.html", 12_100, At(2026, 11, 4, 11, 48)),
    ];

    private static List<FileEntry> RemoteEntries(DemoFileSource source) =>
    [
        FolderEntry(source, RemoteFolder, "css", At(2026, 11, 5, 18, 25), Directory755),
        FolderEntry(source, RemoteFolder, "images", At(2026, 11, 6, 12, 32), Directory755),
        FolderEntry(source, RemoteFolder, "js", At(2026, 10, 28, 9, 15), Directory755),
        FolderEntry(source, RemoteFolder, "商品写真", At(2026, 11, 6, 12, 36), Directory755),
        FileEntryAt(source, RemoteFolder, ".htaccess", 1_536, At(2026, 8, 12, 14, 21), File644),
        FileEntryAt(source, RemoteFolder, "about.html", 9_100, At(2026, 11, 2, 16, 6), File644),
        FileEntryAt(source, RemoteFolder, "index.html", 18_300, At(2026, 11, 6, 12, 31), File644),
        FileEntryAt(source, RemoteFolder, "logo.png", 46_800, At(2026, 10, 20, 10, 13), File644),
        FileEntryAt(source, RemoteFolder, "robots.txt", 120, At(2026, 9, 30, 8, 1), File644),
        FileEntryAt(source, RemoteFolder, "sitemap.xml", 3_900, At(2026, 11, 6, 12, 31), File644),
        FileEntryAt(source, RemoteFolder, "お問い合わせ.html", 12_100, At(2026, 11, 4, 11, 49), File644),
    ];

    // 500 件のアップロードの途中: 完了 377、失敗 2、転送中 2、再試行待ち 1、待機中 118
    private static (List<TransferItemSnapshot> Items, List<(Guid Id, double BytesPerSecond)> Rates) TransferItems()
    {
        var items = new List<TransferItemSnapshot>(500);
        var rates = new List<(Guid, double)>();
        long sequence = 0;

        TransferItemSnapshot Upload(string folder, string name, TransferState state, long total, long transferred,
            int failedAttempts = 0, TransferFailure? failure = null, DateTimeOffset? retryAt = null, DateTimeOffset? finishedAt = null)
        {
            var localFolder = folder == PhotoFolder ? LocalPhotoFolder : LocalFolder + folder[RemoteFolder.Length..].Replace('/', '\\');
            var snapshot = new TransferItemSnapshot(
                Guid.NewGuid(), sequence++, TransferDirection.Upload, localFolder + "\\" + name, folder + "/" + name,
                state, failedAttempts, failure, retryAt, total, transferred, Note: null,
                SizeVerified: state == TransferState.Completed, FinishedAt: finishedAt);
            items.Add(snapshot);
            return snapshot;
        }

        // 完了（12:05〜12:35）。いくつかはデザイン案と同じ名前にする
        var start = At(2026, 11, 6, 12, 5);
        for (var i = 0; i < 379; i++)
        {
            var finishedAt = start + TimeSpan.FromSeconds(i * 1800d / 378);
            switch (i)
            {
                case 30:
                    Upload("/var/www/html/admin", "config.php", TransferState.Failed, 4_200, 0, 1,
                        FtpReplyClassifier.Classify(550, "Permission denied."), finishedAt: finishedAt);
                    break;
                case 150:
                    Upload("/var/www/html/video", "紹介動画.mp4", TransferState.Failed, 486_000_000, 120_000_000, 1,
                        FtpReplyClassifier.Classify(552, "Disk full."), finishedAt: finishedAt);
                    break;
                case 200:
                {
                    var snapshot = new TransferItemSnapshot(
                        Guid.NewGuid(), sequence++, TransferDirection.Download, LocalFolder + @"\logs\access.log", "/var/log/access.log",
                        TransferState.Completed, 0, null, null, 2_400_000, 2_400_000, null, SizeVerified: true, FinishedAt: At(2026, 11, 6, 12, 20));
                    items.Add(snapshot);
                    break;
                }
                case 320:
                    Upload(RemoteFolder, "index.html", TransferState.Completed, 18_300, 18_300, finishedAt: At(2026, 11, 6, 12, 31));
                    break;
                case 378:
                    Upload(PhotoFolder, "winter-boots-03.jpg", TransferState.Completed, 11_800_000, 11_800_000, finishedAt: At(2026, 11, 6, 12, 35));
                    break;
                default:
                {
                    var size = 9_000_000 + i * 7_919 % 4_000_000;
                    Upload(PhotoFolder, $"item-{i + 1:000}.jpg", TransferState.Completed, size, size, finishedAt: finishedAt);
                    break;
                }
            }
        }

        // 転送中 2 件（3.4 MB/s と 2.9 MB/s）
        var coat1 = Upload(PhotoFolder, "winter-coat-01.jpg", TransferState.Running, 18_000_000, 11_160_000);
        var coat2 = Upload(PhotoFolder, "winter-coat-02.jpg", TransferState.Running, 14_800_000, 2_664_000);
        rates.Add((coat1.Id, 3.4 * 1024 * 1024));
        rates.Add((coat2.Id, 2.9 * 1024 * 1024));

        // 接続が切れて、8 秒後にやり直す 1 件
        Upload(PhotoFolder, "scarf-red-05.jpg", TransferState.WaitingToRetry, 12_000_000, 4_800_000, 1,
            TransferFailure.Transient(FailureReason.ConnectionLost), retryAt: Now + TimeSpan.FromSeconds(8));

        // 待機中 118 件
        for (var i = 6; i < 6 + 118; i++)
            Upload(PhotoFolder, $"scarf-red-{i:00}.jpg", TransferState.Queued, 11_500_000, 0);

        return (items, rates);
    }
}
