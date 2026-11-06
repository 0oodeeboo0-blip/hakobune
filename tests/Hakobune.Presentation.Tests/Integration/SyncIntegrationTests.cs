using Hakobune.Core.Browsing;
using Hakobune.Core.Remote;
using Hakobune.Core.Tests.Integration;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Integration;

/// <summary>
/// 本物のサーバー（vsftpd、Pure-FTPd、ProFTPD、OpenSSH）で、同期の比べ方を確かめる。
/// 送った直後に比べ直すと何も送らないこと（サーバーごとの時刻の細かさや形に惑わされない）と、
/// 変えたものだけを送り、消したものだけを消すこと。
/// </summary>
[Trait("Category", "Integration")]
public sealed class SyncIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> Servers => new() { "VSFTPD", "PUREFTPD", "PROFTPD", "SFTP" };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Write(string relativePath, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_root, "site", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    // 「種類 位置」を、比べやすいように並べ替えて返す
    private static string[] Describe(SyncPlan plan) =>
        [.. plan.Items.Select(item => $"{item.Change} {item.RelativePath}").Order(StringComparer.Ordinal)];

    private static async Task RunAsync(IRemoteSessionFactory factory, IReadOnlyList<TransferRequest> requests, CancellationToken cancellationToken)
    {
        var scheduler = new TransferScheduler(factory, new TransferOptions { MaxConcurrency = 2 });
        scheduler.Enqueue(requests);
        var result = await scheduler.RunAsync(cancellationToken);
        Assert.Equal(TransferRunOutcome.Finished, result.Outcome);
        Assert.Equal(requests.Count, result.Summary.Completed);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task After_sending_nothing_is_sent_again_until_something_changes(string name)
    {
        var server = TestServers.Find(name);
        if (server is null)
        {
            Assert.Skip(TestServers.SkipReason(name));
            return;
        }
        var cancellationToken = TestContext.Current.CancellationToken;

        string remoteRoot;
        await using (var session = server.Factory.Create())
        {
            await session.ConnectAsync(cancellationToken);
            remoteRoot = await TestServers.CreateTestDirectoryAsync(session, cancellationToken);
        }

        // 1 時間前に作ったファイルと、2 年前のファイル（FTP の LIST では、古いファイルは日付までしか分からない）
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        Write("index.html", "<h1>こんにちは</h1>", hourAgo);
        Write("css/site.css", "body { color: #333; }", hourAgo);
        Write("画像/商品 01.txt", "商品の説明", hourAgo);
        Write("old.txt", "old", DateTime.UtcNow.AddYears(-2));
        Write("Thumbs.db", "thumbs", hourAgo);
        var site = Path.Combine(_root, "site");
        Directory.CreateDirectory(Path.Combine(site, "empty"));
        var exclusions = new ExclusionRules(["Thumbs.db"]);

        var local = new LocalFileSource(showComputerRoot: false);
        await using var browser = new RemoteBrowser(server.Factory);
        var remote = new RemoteFileSource(browser, server.Host);
        Task<SyncPlan> PlanUploadAsync() =>
            SyncPlanner.PlanAsync(SyncDirection.Upload, local, site, remote, remoteRoot, exclusions, null, cancellationToken);

        // はじめは、送らないファイルのほかは全部が新しい
        var plan = await PlanUploadAsync();
        Assert.Equal(
            new[] { "New css/site.css", "New empty", "New index.html", "New old.txt", "New 画像/商品 01.txt" },
            Describe(plan));
        Assert.Equal(1, plan.ExcludedCount);
        await RunAsync(server.Factory, SyncPlanner.ToRequests(plan, includeTargetNewer: false, deleteOnlyOnTarget: false), cancellationToken);

        // 送った直後に比べ直すと、何も送らない
        plan = await PlanUploadAsync();
        Assert.Equal(Array.Empty<string>(), Describe(plan));
        Assert.Equal(4, plan.SameCount);
        Assert.Empty(plan.UnreadableFolders);

        // 直す・足す・消す
        Write("index.html", "<h1>こんにちは、また</h1>", DateTime.UtcNow);
        Write("news.html", "お知らせ", DateTime.UtcNow);
        File.Delete(Path.Combine(site, "old.txt"));
        plan = await PlanUploadAsync();
        Assert.Equal(new[] { "Changed index.html", "New news.html", "OnlyOnTarget old.txt" }, Describe(plan));
        await RunAsync(server.Factory, SyncPlanner.ToRequests(plan, includeTargetNewer: false, deleteOnlyOnTarget: true), cancellationToken);

        plan = await PlanUploadAsync();
        Assert.Equal(Array.Empty<string>(), Describe(plan));
        Assert.Equal(4, plan.SameCount);

        // 受け取る向き: 空のフォルダに受け取ると全部が新しく、受け取った直後に比べ直すと何もない
        var copy = Path.Combine(_root, "copy");
        Directory.CreateDirectory(copy);
        Task<SyncPlan> PlanDownloadAsync() =>
            SyncPlanner.PlanAsync(SyncDirection.Download, remote, remoteRoot, local, copy, exclusions, null, cancellationToken);
        plan = await PlanDownloadAsync();
        Assert.Equal(
            new[] { "New css/site.css", "New empty", "New index.html", "New news.html", "New 画像/商品 01.txt" },
            Describe(plan));
        await RunAsync(server.Factory, SyncPlanner.ToRequests(plan, includeTargetNewer: false, deleteOnlyOnTarget: false), cancellationToken);
        Assert.Equal("<h1>こんにちは、また</h1>", await File.ReadAllTextAsync(Path.Combine(copy, "index.html"), cancellationToken));
        Assert.True(Directory.Exists(Path.Combine(copy, "empty")));

        plan = await PlanDownloadAsync();
        Assert.Equal(Array.Empty<string>(), Describe(plan));
        Assert.Equal(4, plan.SameCount);

        await using (var session = server.Factory.Create())
        {
            await session.ConnectAsync(cancellationToken);
            await session.DeleteDirectoryAsync(remoteRoot, cancellationToken);
        }
    }
}
