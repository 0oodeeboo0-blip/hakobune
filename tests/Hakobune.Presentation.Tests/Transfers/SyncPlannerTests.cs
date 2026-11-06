using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Transfers;

/// <summary>同期の下準備（左右のフォルダを比べて、何を送るか決める）。</summary>
public sealed class SyncPlannerTests
{
    // 秒の端数のある時刻（パソコンや SFTP の時刻のように、秒まで分かるもの）
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 17, TimeSpan.Zero);

    private readonly FakeFileSource _local = new();
    private readonly FakeFileSource _server = new();

    private Task<SyncPlan> PlanUploadAsync(ExclusionRules? exclusions = null) =>
        SyncPlanner.PlanAsync(
            SyncDirection.Upload, _local, "/site", _server, "/www", exclusions ?? ExclusionRules.None, null, TestContext.Current.CancellationToken);

    // 「種類 位置」を、比べやすいように並べ替えて返す
    private static string[] Describe(SyncPlan plan) =>
        [.. plan.Items.Select(item => $"{item.Change} {item.RelativePath}").Order(StringComparer.Ordinal)];

    private static DateTimeOffset ToMinute(DateTimeOffset time) =>
        new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Offset);

    [Fact]
    public async Task New_changed_and_unchanged_files_are_told_apart()
    {
        _local.Folders["/site"] =
        [
            FakeFileSource.File("/site", "a.txt", 10, T0.AddHours(1)), // 直した（大きさは同じ）
            FakeFileSource.File("/site", "b.txt", 5, T0), // 同じ
            FakeFileSource.File("/site", "c.txt", 3, T0), // サーバーのほうで直した
            FakeFileSource.File("/site", "d.txt", 7, T0.AddMinutes(5)), // 大きさも日時も違う
            FakeFileSource.Folder("/site", "sub"),
        ];
        _local.Folders["/site/sub"] = [FakeFileSource.File("/site/sub", "e.txt", 1, T0)];
        _server.Folders["/www"] =
        [
            FakeFileSource.File("/www", "a.txt", 10, T0),
            FakeFileSource.File("/www", "b.txt", 5, T0.AddSeconds(1)), // 1 秒のずれは同じとみなす
            FakeFileSource.File("/www", "c.txt", 4, T0.AddHours(2)),
            FakeFileSource.File("/www", "d.txt", 6, T0),
            FakeFileSource.File("/www", "old.txt", 2, T0),
            FakeFileSource.Folder("/www", "old"),
        ];
        _server.Folders["/www/old"] = [FakeFileSource.File("/www/old", "x.txt", 1, T0)];

        var plan = await PlanUploadAsync();

        Assert.Equal(
            new[] { "Changed a.txt", "Changed d.txt", "New sub/e.txt", "OnlyOnTarget old", "OnlyOnTarget old.txt", "TargetNewer c.txt" },
            Describe(plan));
        Assert.Equal(1, plan.SameCount);
        Assert.Equal(TimeSpan.Zero, plan.ClockOffset);
        Assert.Empty(plan.UnreadableFolders);
        Assert.False(plan.TooMany);
        // サーバーにだけあるフォルダは、中まで読まずに 1 つと数える
        Assert.True(Assert.Single(plan.Items, item => item.RelativePath == "old").IsDirectory);

        // ふつうは、新しいものと変わったものだけを送る
        var requests = SyncPlanner.ToRequests(plan, includeTargetNewer: false, deleteOnlyOnTarget: false);
        Assert.Equal(new[] { "/www/a.txt", "/www/d.txt", "/www/sub/e.txt" }, requests.Select(request => request.RemotePath).Order(StringComparer.Ordinal).ToArray());
        Assert.All(requests, request => Assert.Equal(TransferDirection.Upload, request.Direction));
        Assert.All(requests, request => Assert.Equal(ConflictPolicy.Overwrite, request.OnConflict));
        var e = requests.Single(request => request.RemotePath == "/www/sub/e.txt");
        Assert.Equal("/site/sub/e.txt", e.LocalPath);
        Assert.Equal(1L, e.ExpectedBytes);

        // 選べば、サーバーのほうが新しいものも送り、サーバーにだけあるものを消す（消すのは最後）
        var all = SyncPlanner.ToRequests(plan, includeTargetNewer: true, deleteOnlyOnTarget: true);
        Assert.Equal(6, all.Count);
        Assert.Contains(all, request => request.RemotePath == "/www/c.txt" && !request.IsDelete);
        Assert.Equal(new[] { "/www/old", "/www/old.txt" }, all.TakeLast(2).Select(request => request.RemotePath).Order(StringComparer.Ordinal).ToArray());
        Assert.All(all.TakeLast(2), request => Assert.True(request.IsDelete));
        Assert.All(all.SkipLast(2), request => Assert.False(request.IsDelete));
    }

    [Fact]
    public async Task Empty_folders_are_made_and_mismatched_kinds_are_left_alone()
    {
        _local.Folders["/site"] =
        [
            FakeFileSource.Folder("/site", "uploads"), // 空
            FakeFileSource.File("/site", "x", 1, T0), // サーバーではフォルダ
            FakeFileSource.Folder("/site", "y"), // サーバーではファイル
        ];
        _local.Folders["/site/uploads"] = [];
        _local.Folders["/site/y"] = [FakeFileSource.File("/site/y", "z.txt", 1, T0)];
        _server.Folders["/www"] =
        [
            FakeFileSource.Folder("/www", "x"),
            FakeFileSource.File("/www", "y", 1, T0),
        ];
        _server.Folders["/www/x"] = [];

        var plan = await PlanUploadAsync();

        Assert.Equal(new[] { "Conflict x", "Conflict y", "New uploads" }, Describe(plan));
        var folder = Assert.Single(SyncPlanner.ToRequests(plan, includeTargetNewer: true, deleteOnlyOnTarget: true));
        Assert.True(folder.IsFolder);
        Assert.Equal("/www/uploads", folder.RemotePath);
        Assert.Equal("/site/uploads", folder.LocalPath);
    }

    [Fact]
    public async Task Names_not_to_send_are_neither_compared_nor_deleted()
    {
        _local.Folders["/site"] =
        [
            FakeFileSource.File("/site", "index.html", 5, T0),
            FakeFileSource.File("/site", "Thumbs.db", 9, T0.AddHours(1)),
        ];
        _server.Folders["/www"] =
        [
            FakeFileSource.File("/www", "index.html", 5, T0),
            FakeFileSource.File("/www", "Thumbs.db", 3, T0),
            FakeFileSource.Folder("/www", "node_modules"),
        ];

        var plan = await PlanUploadAsync(new ExclusionRules(["Thumbs.db", "node_modules/"]));

        Assert.Empty(plan.Items);
        Assert.Equal(1, plan.SameCount);
        Assert.Equal(1, plan.ExcludedCount);
    }

    [Fact]
    public async Task A_server_clock_hours_off_is_noticed_and_allowed_for()
    {
        // FTP の LIST の時刻は、サーバーの地域の時刻（日本時間）のまま、分までで届く。UTC として読むと 9 時間先になる
        var local = new List<FileEntry>();
        var remote = new List<FileEntry>();
        for (var i = 1; i <= 4; i++)
        {
            local.Add(FakeFileSource.File("/site", $"p{i}.html", 100 + i, T0.AddMinutes(i)));
            remote.Add(FakeFileSource.File("/www", $"p{i}.html", 100 + i, ToMinute(T0.AddMinutes(i)).AddHours(9)));
        }
        // これだけは、送ったあとにパソコンで直した（大きさは同じ）
        local.Add(FakeFileSource.File("/site", "q.html", 50, T0.AddHours(1)));
        remote.Add(FakeFileSource.File("/www", "q.html", 50, ToMinute(T0).AddHours(9)));
        _local.Folders["/site"] = local;
        _server.Folders["/www"] = remote;

        var plan = await PlanUploadAsync();

        Assert.Equal(TimeSpan.FromHours(-9), plan.ClockOffset);
        Assert.Equal(new[] { "Changed q.html" }, Describe(plan));
        Assert.Equal(4, plan.SameCount);
    }

    [Fact]
    public void Times_known_only_to_the_minute_or_the_day_are_compared_loosely()
    {
        var source = new FileEntry("a", "/a", FileEntryType.File, 10, new DateTimeOffset(2026, 11, 1, 10, 0, 45, TimeSpan.Zero));
        static FileEntry Target(long size, DateTimeOffset modified) => new("a", "/b", FileEntryType.File, size, modified);

        // 分までの時刻（秒が 0）なら、1 分ほどの違いは同じとみなす
        Assert.Null(SyncPlanner.Compare(source, Target(10, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero)), TimeSpan.Zero));
        Assert.Equal(SyncChange.Changed, SyncPlanner.Compare(source, Target(10, new DateTimeOffset(2026, 11, 1, 9, 57, 0, TimeSpan.Zero)), TimeSpan.Zero));
        // 日付までの時刻（半年より前のファイルの LIST）は、大きさだけで比べる
        Assert.Null(SyncPlanner.Compare(source, Target(10, new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero)), TimeSpan.Zero));
        Assert.Equal(SyncChange.Changed, SyncPlanner.Compare(source, Target(11, new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero)), TimeSpan.Zero));
        // 日時が分からなくても、大きさで比べる
        Assert.Null(SyncPlanner.Compare(source with { Modified = null }, Target(10, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), TimeSpan.Zero));

        // 秒まで分かるなら、2 秒より大きい違いで新しいとみなす
        var precise = source with { Modified = new DateTimeOffset(2026, 11, 1, 10, 0, 45, 300, TimeSpan.Zero) };
        Assert.Null(SyncPlanner.Compare(precise, Target(10, new DateTimeOffset(2026, 11, 1, 10, 0, 44, TimeSpan.Zero)), TimeSpan.Zero));
        Assert.Equal(SyncChange.Changed, SyncPlanner.Compare(precise, Target(10, new DateTimeOffset(2026, 11, 1, 10, 0, 40, TimeSpan.Zero)), TimeSpan.Zero));
        // 大きさが違い、送り先のほうが新しい
        Assert.Equal(SyncChange.TargetNewer, SyncPlanner.Compare(precise, Target(9, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero)), TimeSpan.Zero));
        // 時計のずれを直してから比べる
        Assert.Null(SyncPlanner.Compare(precise, Target(10, new DateTimeOffset(2026, 11, 1, 19, 0, 44, TimeSpan.Zero)), TimeSpan.FromHours(-9)));
    }

    [Fact]
    public void A_clock_offset_needs_most_files_to_agree()
    {
        static (FileEntry, FileEntry) Pair(int minutesOff) =>
            (new FileEntry("a", "/a", FileEntryType.File, 1, T0), new FileEntry("a", "/b", FileEntryType.File, 1, T0.AddMinutes(-minutesOff)));

        Assert.Equal(TimeSpan.FromHours(9), SyncPlanner.DetectClockOffset([Pair(540), Pair(540), Pair(540), Pair(3)]));
        // 半分に届かない、15 分刻みでない、少なすぎる、ときは、ずれとみなさない
        Assert.Equal(TimeSpan.Zero, SyncPlanner.DetectClockOffset([Pair(540), Pair(540), Pair(2), Pair(3)]));
        Assert.Equal(TimeSpan.Zero, SyncPlanner.DetectClockOffset([Pair(7), Pair(7), Pair(7)]));
        Assert.Equal(TimeSpan.Zero, SyncPlanner.DetectClockOffset([Pair(540), Pair(540)]));
    }

    [Fact]
    public async Task Downloads_match_names_without_case_and_make_names_windows_can_use()
    {
        // 受け取るときは、送るほうがサーバー、送り先がパソコン
        _server.Folders["/www"] =
        [
            FakeFileSource.File("/www", "Index.HTML", 5, T0),
            FakeFileSource.File("/www", "a:b.txt", 3, T0),
            FakeFileSource.Folder("/www", "img"),
        ];
        _server.Folders["/www/img"] = [FakeFileSource.File("/www/img", "logo.png", 8, T0)];
        _local.Folders["/site"] =
        [
            FakeFileSource.File("/site", "index.html", 5, T0),
            FakeFileSource.File("/site", "only-here.txt", 1, T0),
        ];

        var plan = await SyncPlanner.PlanAsync(
            SyncDirection.Download, _server, "/www", _local, "/site", ExclusionRules.None, null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "New a:b.txt", "New img/logo.png", "OnlyOnTarget only-here.txt" }, Describe(plan));
        Assert.Equal(1, plan.SameCount);
        // パソコン側のものは、選んでも消さない
        var requests = SyncPlanner.ToRequests(plan, includeTargetNewer: true, deleteOnlyOnTarget: true);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(TransferDirection.Download, request.Direction));
        Assert.Equal(Path.Combine("/site", "a_b.txt"), requests.Single(request => request.RemotePath == "/www/a:b.txt").LocalPath);
        Assert.Equal(Path.Combine("/site", "img", "logo.png"), requests.Single(request => request.RemotePath == "/www/img/logo.png").LocalPath);
    }

    [Fact]
    public async Task Links_and_names_that_differ_only_in_case_are_never_deleted()
    {
        _local.Folders["/site"] =
        [
            new FileEntry("shared", "/site/shared", FileEntryType.Link, 0, T0),
            FakeFileSource.File("/site", "Index.html", 5, T0),
        ];
        _server.Folders["/www"] =
        [
            FakeFileSource.Folder("/www", "shared"),
            FakeFileSource.File("/www", "index.html", 4, T0),
            new FileEntry("www", "/www/www", FileEntryType.Link, 11, T0),
        ];
        _server.Folders["/www/shared"] = [FakeFileSource.File("/www/shared", "x", 1, T0)];

        var plan = await PlanUploadAsync();

        Assert.Equal(new[] { "New Index.html" }, Describe(plan));
        Assert.Equal(1, plan.SkippedLinks);
        Assert.Single(SyncPlanner.ToRequests(plan, includeTargetNewer: true, deleteOnlyOnTarget: true));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_is_left_alone()
    {
        _local.Folders["/site"] =
        [
            FakeFileSource.Folder("/site", "private"),
            FakeFileSource.File("/site", "a.txt", 1, T0),
        ];
        _local.Folders["/site/private"] = [FakeFileSource.File("/site/private", "b.txt", 1, T0)];
        _server.Folders["/www"] = [FakeFileSource.Folder("/www", "private")];
        _server.Fault = path => path == "/www/private" ? TransferFailure.Permanent(FailureReason.PermissionDenied) : null;

        var plan = await PlanUploadAsync();

        Assert.Equal(new[] { "New a.txt" }, Describe(plan));
        Assert.Equal(new[] { "/www/private" }, plan.UnreadableFolders.ToArray());
    }

    [Fact]
    public async Task The_open_folders_must_be_readable()
    {
        _local.Folders["/site"] = [FakeFileSource.File("/site", "a.txt", 1, T0)];
        _server.Fault = _ => TransferFailure.Transient(FailureReason.ConnectionLost);

        var error = await Assert.ThrowsAsync<RemoteOperationException>(() => PlanUploadAsync());

        Assert.Equal(FailureReason.ConnectionLost, error.Failure.Reason);
    }

    [Fact]
    public async Task Stopping_stops_the_comparison()
    {
        _local.Folders["/site"] = [FakeFileSource.File("/site", "a.txt", 1, T0)];
        _server.Folders["/www"] = [];
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SyncPlanner.PlanAsync(SyncDirection.Upload, _local, "/site", _server, "/www", ExclusionRules.None, null, cancel.Token));
    }
}
