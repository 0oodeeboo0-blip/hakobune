using System.Text;
using Hakobune.Core.Backups;
using Hakobune.Core.Remote;
using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Backups;

/// <summary>上書き・削除の前の控えの置き場。</summary>
public sealed class BackupStoreTests : IDisposable
{
    private static readonly Guid Shop = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Blog = new("66666666-7777-8888-9999-000000000000");

    private readonly TempDirectory _temp = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    private string Root => Path.Combine(_temp.Path, "backups");

    private BackupStore NewStore(BackupLimits? limits = null) => new(Root, limits, _clock);

    private static Task<BackupResult> SaveTextAsync(BackupStore store, Guid connection, Guid? batch, string path, string text, BackupKind kind = BackupKind.ServerOverwritten)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return store.SaveAsync(
            connection, batch, kind, path, bytes.Length, new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero), bytes.Length + 1,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Backups_are_kept_with_their_operation_and_survive_a_restart()
    {
        var store = NewStore();
        var batch = store.BeginBatch(Shop, "同期（パソコン → サーバー）");
        var first = await SaveTextAsync(store, Shop, batch, "/www/index.html", "<h1>old</h1>");
        _clock.Advance(TimeSpan.FromSeconds(5));
        var second = await SaveTextAsync(store, Shop, batch, "/www/old.html", "gone", BackupKind.ServerDeleted);
        await SaveTextAsync(store, Blog, null, "/blog/a.txt", "blog");

        Assert.Equal(BackupStore.TakenNote, first.Note);
        Assert.Equal("<h1>old</h1>", await File.ReadAllTextAsync(store.GetFullPath(first.Entry!), TestContext.Current.CancellationToken));
        // 控えのファイルの日時は、元のファイルの日時にする（エクスプローラーで見ても分かるように）
        Assert.Equal(new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(store.GetFullPath(first.Entry!)));

        // 読み込み直しても、同じ控えが同じ順（新しいものから）に並ぶ
        var reloaded = NewStore();
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        var entries = reloaded.GetEntries(Shop);
        Assert.Equal(new[] { second.Entry!.Id, first.Entry!.Id }, entries.Select(entry => entry.Id).ToArray());
        Assert.Equal("同期（パソコン → サーバー）", reloaded.GetBatch(entries[0].Batch)?.Label);
        Assert.Equal(BackupKind.ServerDeleted, entries[0].Kind);
        Assert.Equal(13L, entries[1].ReplacementSize);
        Assert.Single(reloaded.GetEntries(Blog));
    }

    [Fact]
    public async Task Nothing_is_kept_when_turned_off_and_big_files_are_skipped_with_a_note()
    {
        var off = NewStore(new BackupLimits { IsEnabled = false });
        var none = await SaveTextAsync(off, Shop, null, "/www/a.txt", "a");
        Assert.Null(none.Entry);
        Assert.Null(none.Note);
        Assert.Empty(off.GetEntries(Shop));

        var small = NewStore(new BackupLimits { MaxFileBytes = 3 });
        var big = await SaveTextAsync(small, Shop, null, "/www/big.txt", "1234");
        Assert.Null(big.Entry);
        Assert.StartsWith("大きいので控えなし", big.Note, StringComparison.Ordinal);
        Assert.Empty(small.GetEntries(Shop));
    }

    [Fact]
    public async Task Old_backups_and_backups_over_the_size_limit_are_removed_oldest_first()
    {
        var store = NewStore(new BackupLimits { KeepFor = TimeSpan.FromDays(30), MaxTotalBytes = 10 });
        var oldest = await SaveTextAsync(store, Shop, null, "/www/1.txt", "1111");
        _clock.Advance(TimeSpan.FromHours(1));
        var middle = await SaveTextAsync(store, Shop, null, "/www/2.txt", "2222");
        _clock.Advance(TimeSpan.FromHours(1));
        // 合計が 12 バイトになり、上限（10）を超えたので、いちばん古いものを消す
        var newest = await SaveTextAsync(store, Shop, null, "/www/3.txt", "3333");

        Assert.Equal(new[] { newest.Entry!.Id, middle.Entry!.Id }, store.GetEntries(Shop).Select(entry => entry.Id).ToArray());
        Assert.False(File.Exists(store.GetFullPath(oldest.Entry!)));

        // 取ったばかり（10 分以内）のものは、上限を超えていても消さない
        var burst = await SaveTextAsync(store, Shop, null, "/www/4.txt", "4444");
        Assert.Contains(store.GetEntries(Shop), entry => entry.Id == burst.Entry!.Id);
        Assert.Contains(store.GetEntries(Shop), entry => entry.Id == newest.Entry!.Id);

        // 30 日を過ぎたら消す
        _clock.Advance(TimeSpan.FromDays(31));
        store.Prune();
        Assert.Empty(store.GetEntries(Shop));
        Assert.False(File.Exists(store.GetFullPath(newest.Entry!)));

        // 消したものは、読み込み直しても戻らない
        var reloaded = NewStore();
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(reloaded.GetEntries(Shop));
    }

    [Fact]
    public async Task Backups_can_be_deleted_and_missing_files_are_dropped_on_load()
    {
        var store = NewStore();
        var keep = await SaveTextAsync(store, Shop, null, "/www/keep.txt", "keep");
        var remove = await SaveTextAsync(store, Shop, null, "/www/remove.txt", "remove");
        var lost = await SaveTextAsync(store, Shop, null, "/www/lost.txt", "lost");

        store.Delete([remove.Entry!.Id]);
        File.Delete(store.GetFullPath(lost.Entry!));
        var reloaded = NewStore();
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(keep.Entry!.Id, Assert.Single(reloaded.GetEntries(Shop)).Id);
        Assert.False(File.Exists(store.GetFullPath(remove.Entry!)));
    }

    [Fact]
    public async Task A_failed_backup_leaves_nothing_behind()
    {
        var store = NewStore();

        await Assert.ThrowsAsync<RemoteOperationException>(() => store.SaveAsync(
            Shop, null, BackupKind.ServerOverwritten, "/www/a.txt", 10, null, null,
            async (stream, token) =>
            {
                await stream.WriteAsync("half"u8.ToArray(), token);
                throw new RemoteOperationException(TransferFailure.Transient(FailureReason.ConnectionLost));
            },
            TestContext.Current.CancellationToken));

        Assert.Empty(store.GetEntries(Shop));
        Assert.Empty(Directory.EnumerateFiles(Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_server_folder_is_kept_with_everything_inside()
    {
        var store = NewStore(new BackupLimits { MaxFileBytes = 5 });
        var folders = new Dictionary<string, IReadOnlyList<RemoteItem>>
        {
            ["/www/old"] =
            [
                new RemoteItem("a.txt", "/www/old/a.txt", RemoteItemType.File, 1, null),
                new RemoteItem("sub", "/www/old/sub", RemoteItemType.Directory, 0, null),
                new RemoteItem("link", "/www/old/link", RemoteItemType.Link, 0, null),
                new RemoteItem("big.bin", "/www/old/big.bin", RemoteItemType.File, 100, null),
            ],
            ["/www/old/sub"] = [new RemoteItem("b.txt", "/www/old/sub/b.txt", RemoteItemType.File, 1, null)],
        };
        var contents = new Dictionary<string, string> { ["/www/old/a.txt"] = "a", ["/www/old/sub/b.txt"] = "b" };

        var note = await store.SaveServerFolderAsync(
            Shop,
            null,
            "/www/old",
            (path, _) => Task.FromResult(folders[path]),
            (path, stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(contents[path]), token).AsTask(),
            TestContext.Current.CancellationToken);

        Assert.Equal("控えあり（大きい 1 件は控えなし）", note);
        var entries = store.GetEntries(Shop);
        Assert.Equal(new[] { "/www/old/a.txt", "/www/old/sub/b.txt" }, entries.Select(entry => entry.OriginalPath).Order(StringComparer.Ordinal).ToArray());
        Assert.All(entries, entry => Assert.Equal(BackupKind.ServerDeleted, entry.Kind));
        Assert.Equal("b", await File.ReadAllTextAsync(store.GetFullPath(entries.Single(entry => entry.OriginalPath.EndsWith("b.txt", StringComparison.Ordinal))), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_computer_file_is_copied()
    {
        var store = NewStore();
        var local = _temp.CreateFile("site/index.html", "<p>local</p>"u8.ToArray());

        var result = await store.SaveLocalAsync(Shop, null, local, 20, TestContext.Current.CancellationToken);

        Assert.Equal(BackupKind.LocalOverwritten, result.Entry?.Kind);
        Assert.False(result.Entry!.IsOnServer);
        Assert.Equal("<p>local</p>", await File.ReadAllTextAsync(store.GetFullPath(result.Entry), TestContext.Current.CancellationToken));
        Assert.Null((await store.SaveLocalAsync(Shop, null, Path.Combine(_temp.Path, "missing.txt"), 1, TestContext.Current.CancellationToken)).Entry);
    }

    /// <summary>進め方を決められる時計。</summary>
    internal sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
