using Hakobune.Core.Backups;
using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Transfers;

/// <summary>転送で上書き・削除する前に、元のファイルの控えを取る。</summary>
public sealed class TransferBackupTests : IDisposable
{
    private static readonly Guid Shop = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private BackupStore NewStore() => new(Path.Combine(_temp.Path, "backups"));

    private static TransferOptions Options(BackupStore store) => new()
    {
        MaxConcurrency = 1,
        RetryPolicy = new RetryPolicy(10, [TimeSpan.Zero], jitterRatio: 0),
        ConnectPolicy = new RetryPolicy(3, [TimeSpan.Zero], jitterRatio: 0),
        Backups = new ConnectionBackups(store, Shop),
    };

    private static string Read(BackupStore store, BackupEntry entry) => File.ReadAllText(store.GetFullPath(entry));

    [Fact]
    public async Task Overwriting_a_server_file_keeps_the_old_one_first()
    {
        var store = NewStore();
        var server = new FakeServer();
        server.Files.CreateDirectory("/www");
        server.Files.WriteFile("/www/index.html", "old page"u8, 0, DateTimeOffset.UtcNow.AddDays(-1));
        var changed = _temp.CreateFile("site/index.html", "new page!"u8.ToArray());
        var fresh = _temp.CreateFile("site/new.html", "new"u8.ToArray());
        var batch = store.BeginBatch(Shop, "アップロード");
        var scheduler = new TransferScheduler(server, Options(store));
        scheduler.Enqueue(
        [
            new TransferRequest(TransferDirection.Upload, changed, "/www/index.html", Batch: batch),
            new TransferRequest(TransferDirection.Upload, fresh, "/www/new.html", Batch: batch),
        ]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Summary.Completed);
        Assert.Equal("new page!"u8.ToArray(), server.Files.ReadFile("/www/index.html"));
        // 新しく置いたファイルには、控えは要らない
        var entry = Assert.Single(store.GetEntries(Shop));
        Assert.Equal(BackupKind.ServerOverwritten, entry.Kind);
        Assert.Equal("/www/index.html", entry.OriginalPath);
        Assert.Equal(batch, entry.Batch);
        Assert.Equal(9L, entry.ReplacementSize);
        Assert.Equal("old page", Read(store, entry));
        Assert.Equal(BackupStore.TakenNote, scheduler.GetItems().Single(item => item.RemotePath == "/www/index.html").Note);
        Assert.Null(scheduler.GetItems().Single(item => item.RemotePath == "/www/new.html").Note);
        // 保存して読み戻しても、操作のまとまりは残る
        Assert.All(scheduler.SaveState(), item => Assert.Equal(batch, item.Batch));
    }

    [Fact]
    public async Task A_retried_overwrite_keeps_only_the_original()
    {
        var store = NewStore();
        var attempts = 0;
        var server = new FakeServer
        {
            // 1 回目は半分まで書いたところで切れる。2 回目は続きから送る
            UploadFault = (_, _, length) => Interlocked.Increment(ref attempts) == 1
                ? new WriteFault(length / 2, TransferFailure.Transient(FailureReason.ConnectionLost))
                : null,
        };
        server.Files.CreateDirectory("/www");
        server.Files.WriteFile("/www/data.bin", "original"u8, 0, DateTimeOffset.UtcNow);
        var content = new byte[1000];
        new Random(5).NextBytes(content);
        var local = _temp.CreateFile("data.bin", content);
        var scheduler = new TransferScheduler(server, Options(store));
        scheduler.Enqueue([new TransferRequest(TransferDirection.Upload, local, "/www/data.bin")]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Completed);
        Assert.Equal(content, server.Files.ReadFile("/www/data.bin"));
        var entry = Assert.Single(store.GetEntries(Shop));
        Assert.Equal("original", Read(store, entry));
    }

    [Fact]
    public async Task Deleting_on_the_server_keeps_the_files_first()
    {
        var store = NewStore();
        var server = new FakeServer();
        server.Files.CreateDirectory("/www/campaign/img");
        server.Files.WriteFile("/www/old.html", "old"u8, 0, DateTimeOffset.UtcNow);
        server.Files.WriteFile("/www/campaign/index.html", "campaign"u8, 0, DateTimeOffset.UtcNow);
        server.Files.WriteFile("/www/campaign/img/a.png", "png"u8, 0, DateTimeOffset.UtcNow);
        var scheduler = new TransferScheduler(server, Options(store));
        scheduler.Enqueue(
        [
            new TransferRequest(TransferDirection.Upload, "", "/www/old.html", IsDelete: true),
            new TransferRequest(TransferDirection.Upload, "", "/www/campaign", IsDelete: true),
        ]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Summary.Completed);
        Assert.Null(server.Files.GetItem("/www/campaign"));
        var entries = store.GetEntries(Shop).ToDictionary(entry => entry.OriginalPath);
        Assert.Equal(new[] { "/www/campaign/img/a.png", "/www/campaign/index.html", "/www/old.html" }, entries.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(entries.Values, entry => Assert.Equal(BackupKind.ServerDeleted, entry.Kind));
        Assert.Equal("campaign", Read(store, entries["/www/campaign/index.html"]));
        Assert.Equal("png", Read(store, entries["/www/campaign/img/a.png"]));
        Assert.All(scheduler.GetItems(), item => Assert.Equal(BackupStore.TakenNote, item.Note));
    }

    [Fact]
    public async Task Receiving_over_a_computer_file_keeps_the_old_one_first()
    {
        var store = NewStore();
        var server = new FakeServer();
        server.Files.CreateDirectory("/www");
        server.Files.WriteFile("/www/notes.txt", "from server"u8, 0, DateTimeOffset.UtcNow);
        var local = _temp.CreateFile("site/notes.txt", "mine"u8.ToArray());
        var fresh = Path.Combine(_temp.Path, "site", "fresh.txt");
        server.Files.WriteFile("/www/fresh.txt", "fresh"u8, 0, DateTimeOffset.UtcNow);
        var scheduler = new TransferScheduler(server, Options(store));
        scheduler.Enqueue(
        [
            new TransferRequest(TransferDirection.Download, local, "/www/notes.txt"),
            new TransferRequest(TransferDirection.Download, fresh, "/www/fresh.txt"),
        ]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Summary.Completed);
        Assert.Equal("from server", await File.ReadAllTextAsync(local, TestContext.Current.CancellationToken));
        var entry = Assert.Single(store.GetEntries(Shop));
        Assert.Equal(BackupKind.LocalOverwritten, entry.Kind);
        Assert.Equal(Path.GetFullPath(local), entry.OriginalPath);
        Assert.Equal("mine", Read(store, entry));
    }

    [Fact]
    public async Task Nothing_is_kept_when_backups_are_turned_off()
    {
        var store = new BackupStore(Path.Combine(_temp.Path, "backups"), new BackupLimits { IsEnabled = false });
        var server = new FakeServer();
        server.Files.CreateDirectory("/www");
        server.Files.WriteFile("/www/index.html", "old"u8, 0, DateTimeOffset.UtcNow);
        var local = _temp.CreateFile("index.html", "new"u8.ToArray());
        var scheduler = new TransferScheduler(server, Options(store));
        scheduler.Enqueue(
        [
            new TransferRequest(TransferDirection.Upload, local, "/www/index.html"),
        ]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Completed);
        Assert.Empty(store.GetEntries(Shop));
        Assert.Null(Assert.Single(scheduler.GetItems()).Note);
    }
}
