using System.Text;
using Hakobune.Core.Backups;
using Hakobune.Presentation.Backups;

namespace Hakobune.Presentation.Tests.Backups;

/// <summary>下の一覧の「控え」。</summary>
public sealed class BackupListViewModelTests : IDisposable
{
    private static readonly Guid Shop = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Blog = new("11111111-0000-0000-0000-000000000002");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero));

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

    private static Task<BackupResult> TakeAsync(BackupStore store, Guid connection, Guid? batch, BackupKind kind, string path, string text, DateTimeOffset? modified = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return store.SaveAsync(
            connection, batch, kind, path, bytes.Length, modified, null,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Backups_are_grouped_by_operation_newest_first_for_the_open_connection()
    {
        var store = new BackupStore(_root, timeProvider: _time);
        var upload = store.BeginBatch(Shop, "アップロード");
        await TakeAsync(store, Shop, upload, BackupKind.ServerOverwritten, "/www/about.html", "about", new DateTimeOffset(2025, 12, 24, 1, 0, 0, TimeSpan.Zero));
        _time.Advance(TimeSpan.FromMinutes(30));
        var sync = store.BeginBatch(Shop, "同期（パソコン → サーバー）");
        await TakeAsync(store, Shop, sync, BackupKind.ServerOverwritten, "/www/index.html", "index");
        await TakeAsync(store, Shop, sync, BackupKind.ServerDeleted, "/www/old/a.txt", "a");
        await TakeAsync(store, Blog, null, BackupKind.ServerOverwritten, "/blog/a.txt", "blog");
        var list = new BackupListViewModel(store, _time);

        // つないでいなければ何も出さない
        Assert.False(list.HasGroups);
        Assert.Contains("接続すると", list.EmptyText, StringComparison.Ordinal);

        list.SetConnection(Shop);

        Assert.True(list.HasGroups);
        Assert.Equal(new[] { "同期（パソコン → サーバー）", "アップロード" }, list.Groups.Select(group => group.Title).ToArray());
        var latest = list.Groups[0];
        Assert.Equal("上書き 1・削除 1・6 バイト", latest.SummaryText);
        Assert.Equal("2 件を元に戻す", latest.RestoreAllText);
        Assert.Equal(new[] { "a.txt", "index.html" }, latest.Rows.Select(row => row.Name).Order(StringComparer.Ordinal).ToArray());
        var deleted = latest.Rows.Single(row => row.Name == "a.txt");
        Assert.Equal("削除前", deleted.KindText);
        Assert.Equal("/www/old", deleted.Folder);
        Assert.True(deleted.IsDeleted);
        // 去年のものは年も出す（パソコンの時刻帯で）
        var expected = TimeZoneInfo.ConvertTime(new DateTimeOffset(2025, 12, 24, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Local)
            .ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, list.Groups[1].Rows[0].WhenText);
        Assert.Contains("控え 3 件", list.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_asks_the_owner_and_deleting_removes_the_backups()
    {
        var store = new BackupStore(_root, timeProvider: _time);
        var batch = store.BeginBatch(Shop, "同期（パソコン → サーバー）");
        await TakeAsync(store, Shop, batch, BackupKind.ServerOverwritten, "/www/index.html", "index");
        await TakeAsync(store, Shop, batch, BackupKind.ServerOverwritten, "/www/about.html", "about");
        var list = new BackupListViewModel(store, _time);
        list.SetConnection(Shop);
        var requests = new List<IReadOnlyList<BackupEntry>>();
        list.RestoreRequested += (_, entries) => requests.Add(entries);

        list.Groups[0].Rows[0].RestoreCommand.Execute(null);
        list.Groups[0].RestoreAllCommand.Execute(null);

        Assert.Single(requests[0]);
        Assert.Equal(2, requests[1].Count);

        // 控えが増えると出し直す
        await TakeAsync(store, Shop, batch, BackupKind.ServerDeleted, "/www/old.html", "old");
        Assert.Equal(3, list.Groups[0].Entries.Count);

        list.Groups[0].DeleteCommand.Execute(null);
        Assert.False(list.HasGroups);
        Assert.Empty(store.GetEntries(Shop));
        Assert.Contains("まだ控えはありません", list.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Big_operations_list_only_the_first_rows()
    {
        var store = new BackupStore(_root, timeProvider: _time);
        var batch = store.BeginBatch(Shop, "同期（パソコン → サーバー）");
        for (var i = 0; i < BackupListViewModel.MaxRowsPerGroup + 5; i++)
            await TakeAsync(store, Shop, batch, BackupKind.ServerOverwritten, $"/www/{i}.txt", "x");
        var list = new BackupListViewModel(store, _time);
        list.SetConnection(Shop);

        var group = Assert.Single(list.Groups);
        Assert.Equal(BackupListViewModel.MaxRowsPerGroup, group.Rows.Count);
        Assert.True(group.HasMore);
        Assert.StartsWith("ほか 5 件", group.MoreText, StringComparison.Ordinal);
        Assert.Equal(BackupListViewModel.MaxRowsPerGroup + 5, group.Entries.Count);
    }
}
