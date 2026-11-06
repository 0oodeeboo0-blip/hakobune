using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Transfers;

/// <summary>同期の確認の画面（何を送るか見せ、選んだとおりの依頼にする）。</summary>
public sealed class SyncViewModelTests
{
    // 日本時間の 2026/11/06 12:10
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 3, 10, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "日本", "日本");

    private static FileEntry Local(string relative, long size, DateTimeOffset modified) =>
        new(relative.Split('/')[^1], "/site/" + relative, FileEntryType.File, size, modified);

    private static FileEntry Remote(string relative, long size, DateTimeOffset modified) =>
        new(relative.Split('/')[^1], "/www/" + relative, FileEntryType.File, size, modified);

    private static SyncPlan Plan(SyncDirection direction = SyncDirection.Upload) => new(
        direction,
        "/site",
        "/www",
        [
            new SyncItem(SyncChange.TargetNewer, "contact.html", Local("contact.html", 100, T0.AddDays(-2)), Remote("contact.html", 120, T0)),
            new SyncItem(SyncChange.Changed, "index.html", Local("index.html", 2048, T0), Remote("index.html", 1024, T0.AddDays(-1))),
            new SyncItem(SyncChange.New, "news.html", Local("news.html", 512, T0), null),
            new SyncItem(SyncChange.OnlyOnTarget, "old", null, new FileEntry("old", "/www/old", FileEntryType.Directory, 0, T0)),
            new SyncItem(SyncChange.OnlyOnTarget, "old.html", null, Remote("old.html", 10, T0.AddDays(-30))),
            new SyncItem(SyncChange.New, "uploads", new FileEntry("uploads", "/site/uploads", FileEntryType.Directory, 0, T0), null),
            new SyncItem(SyncChange.Conflict, "x", Local("x", 1, T0), new FileEntry("x", "/www/x", FileEntryType.Directory, 0, T0)),
        ],
        SameCount: 120,
        ExcludedCount: 3,
        UnreadableFolders: [],
        TooMany: false,
        ClockOffset: TimeSpan.FromHours(-9));

    private static async Task<SyncViewModel> ReviewAsync(SyncPlan plan)
    {
        var sync = new SyncViewModel(plan.Direction, "/site", "example.com /www", (_, _) => Task.FromResult(plan), Tokyo);
        sync.Start();
        await sync.Planning;
        return sync;
    }

    [Fact]
    public async Task The_review_lists_what_is_sent_and_the_button_follows_the_choices()
    {
        var sync = await ReviewAsync(Plan());

        Assert.Equal("変わったファイルだけ送る", sync.Title);
        Assert.True(sync.HasResult);
        Assert.False(sync.IsScanning);
        Assert.False(sync.HasError);
        Assert.Equal(new[] { "index.html", "news.html", "uploads/" }, sync.SendRows.Select(row => row.Path).ToArray());
        Assert.Equal(new[] { "更新", "新規", "フォルダ" }, sync.SendRows.Select(row => row.Chip).ToArray());
        Assert.Equal("11/06 12:10（サーバー 11/05 12:10）", sync.SendRows[0].WhenText);
        Assert.Equal("", sync.SendRows[2].SizeText);
        Assert.Equal("新しい 2", sync.NewText);
        Assert.Equal("変わった 1", sync.ChangedText);
        Assert.Equal("サーバーのほうが新しい 1", sync.TargetNewerText);
        Assert.Equal("サーバーにだけある 2", sync.OnlyOnTargetText);
        Assert.Equal("同じ 120（送りません）", sync.SameText);
        Assert.Equal("送るファイル（3）", sync.SendHeader);
        Assert.Equal(new[] { "x" }, sync.ConflictRows.Select(row => row.Path).ToArray());
        Assert.False(sync.ShowsNothingToSend);

        // 送り先のほうが新しいものと、サーバーにだけあるものは、選ばなければそのまま
        Assert.Equal("3 件を送る", sync.PrimaryText);
        Assert.True(sync.CanConfirm);
        Assert.True(sync.CanDeleteOnlyOnTarget);
        Assert.Equal(new[] { "old/", "old.html" }, sync.OnlyOnTargetRows.Select(row => row.Path).ToArray());
        Assert.All(sync.OnlyOnTargetRows, row => Assert.Equal("残す", row.Chip));
        Assert.All(sync.OnlyOnTargetRows, row => Assert.True(row.IsOther));
        Assert.Equal(3, sync.BuildRequests().Count);
        Assert.True(sync.HasNotes);
        Assert.Contains("3 件", sync.Notes[0], StringComparison.Ordinal);
        Assert.Contains(sync.Notes, note => note.Contains("9 時間", StringComparison.Ordinal));

        sync.IncludeTargetNewer = true;
        Assert.Equal("4 件を送る", sync.PrimaryText);

        sync.DeleteOnlyOnTarget = true;
        Assert.Equal("4 件を送り、2 件を消す", sync.PrimaryText);
        Assert.All(sync.OnlyOnTargetRows, row => Assert.Equal("消す", row.Chip));
        Assert.All(sync.OnlyOnTargetRows, row => Assert.True(row.IsDelete));
        Assert.All(sync.OnlyOnTargetRows, row => Assert.False(row.IsOther));
        var requests = sync.BuildRequests();
        Assert.Equal(6, requests.Count);
        Assert.Equal(new[] { "/www/old", "/www/old.html" }, requests.Where(request => request.IsDelete).Select(request => request.RemotePath).ToArray());
    }

    [Fact]
    public async Task Nothing_to_send_is_said_plainly_and_deleting_alone_is_possible()
    {
        var full = Plan();
        var sync = await ReviewAsync(full with { Items = [.. full.Items.Where(item => item.Change == SyncChange.OnlyOnTarget)] });

        Assert.True(sync.ShowsNothingToSend);
        Assert.False(sync.HasSendRows);
        Assert.Equal("送るものはありません", sync.PrimaryText);
        Assert.False(sync.CanConfirm);

        sync.DeleteOnlyOnTarget = true;
        Assert.Equal("2 件を消す", sync.PrimaryText);
        Assert.True(sync.CanConfirm);
        Assert.All(sync.BuildRequests(), request => Assert.True(request.IsDelete));
    }

    [Fact]
    public async Task Receiving_never_offers_to_delete()
    {
        var sync = await ReviewAsync(Plan(SyncDirection.Download));

        Assert.Equal("変わったファイルだけ受け取る", sync.Title);
        Assert.False(sync.CanDeleteOnlyOnTarget);
        Assert.Equal("パソコンにだけある 2", sync.OnlyOnTargetText);
        Assert.Equal("受け取るファイル（3）", sync.SendHeader);
        Assert.Equal("3 件を受け取る", sync.PrimaryText);

        sync.DeleteOnlyOnTarget = true;
        Assert.Equal("3 件を受け取る", sync.PrimaryText);
        Assert.All(sync.OnlyOnTargetRows, row => Assert.Equal("残す", row.Chip));
        Assert.DoesNotContain(sync.BuildRequests(), request => request.IsDelete);
    }

    [Fact]
    public async Task A_failed_comparison_explains_why()
    {
        var sync = new SyncViewModel(
            SyncDirection.Upload,
            "/site",
            "/www",
            (_, _) => Task.FromException<SyncPlan>(new RemoteOperationException(TransferFailure.Transient(FailureReason.ConnectionLost))),
            Tokyo);
        sync.Start();
        await sync.Planning;

        Assert.True(sync.HasError);
        Assert.False(sync.HasResult);
        Assert.False(sync.IsScanning);
        Assert.StartsWith("比べられませんでした: ", sync.ErrorText, StringComparison.Ordinal);
        Assert.False(sync.CanConfirm);
        Assert.Empty(sync.BuildRequests());
    }

    [Fact]
    public async Task The_comparison_shows_how_far_it_has_got_and_can_be_stopped()
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new SyncViewModel(
            SyncDirection.Upload,
            "/site",
            "/www",
            async (progress, cancellationToken) =>
            {
                progress.Report(new SyncScanProgress(18, 42));
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("ここには来ない");
            },
            Tokyo);
        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SyncViewModel.ScanText))
                reported.TrySetResult();
        };

        sync.Start();
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(sync.IsScanning);
        Assert.Equal("フォルダを読んで比べています（18 / 42）", sync.ScanText);
        Assert.Equal("比べています…", sync.PrimaryText);
        Assert.False(sync.CanConfirm);

        sync.Cancel();
        await sync.Planning.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(sync.IsScanning);
        Assert.False(sync.HasResult);
        Assert.Empty(sync.BuildRequests());
    }
}
