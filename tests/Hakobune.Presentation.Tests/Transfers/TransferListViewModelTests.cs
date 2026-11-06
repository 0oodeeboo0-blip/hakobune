using Hakobune.Core.Transfers;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Transfers;

public class TransferListViewModelTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 6, 3, 0, 0, TimeSpan.Zero);

    private static (TransferListViewModel List, FakeTransferSource Source, ManualTime Time) Create()
    {
        var source = new FakeTransferSource();
        var time = new ManualTime(Noon);
        var list = new TransferListViewModel(source, time, context: null);
        return (list, source, time);
    }

    private static string[] Names(TransferListViewModel list) => [.. list.Rows.Select(row => row.Name)];

    [Fact]
    public void Tabs_count_the_items_by_state()
    {
        var (list, source, _) = Create();
        source.Add("run1", TransferState.Running);
        source.Add("run2", TransferState.Running);
        source.Add("retry", TransferState.WaitingToRetry, failedAttempts: 1);
        for (var i = 0; i < 3; i++)
            source.Add($"queued{i}", TransferState.Queued);
        for (var i = 0; i < 4; i++)
            source.Add($"done{i}", TransferState.Completed, transferred: 1000);
        source.Add("skipped", TransferState.Skipped);
        source.Add("failed1", TransferState.Failed, failure: FtpReplyClassifier.Classify(550, "Permission denied."));
        source.Add("failed2", TransferState.Failed, failure: FtpReplyClassifier.Classify(552, "Disk full."));

        Assert.Equal(new[] { "13", "3", "3", "5", "2" }, list.Tabs.Select(tab => tab.CountText).ToArray());
        Assert.True(list.Tabs[4].IsAlert);
        Assert.Equal("完了 5・失敗 2", list.SummaryText);
        Assert.Equal("7 / 13 件", list.CountText);
        Assert.Equal("54%", list.OverallPercentText);
        Assert.Equal("同時転送 3", list.ConcurrencyText);
        Assert.True(list.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public void The_all_tab_shows_active_items_first_then_failures_then_the_queue_then_finished_items()
    {
        var (list, source, _) = Create();
        source.Add("done", TransferState.Completed, finishedAt: Noon);
        source.Add("queued", TransferState.Queued);
        source.Add("failed", TransferState.Failed, failure: TransferFailure.Permanent(FailureReason.PermissionDenied), finishedAt: Noon);
        source.Add("running", TransferState.Running);
        source.Add("retry", TransferState.WaitingToRetry, failedAttempts: 1);

        Assert.Equal(new[] { "running", "retry", "failed", "queued", "done" }, Names(list));
    }

    [Fact]
    public void Rows_move_between_tabs_when_their_state_changes()
    {
        var (list, source, _) = Create();
        var first = source.Add("first", TransferState.Queued);
        source.Add("second", TransferState.Queued);
        source.Add("third", TransferState.Queued);
        list.SelectTab(TransferTab.Waiting);

        Assert.Equal(new[] { "first", "second", "third" }, Names(list));
        Assert.Equal(new[] { "1番目", "2番目", "3番目" }, list.Rows.Select(row => row.Detail).ToArray());

        // 1 件目が始まると、待機中から外れ、順番が繰り上がる
        source.Update(first with { State = TransferState.Running });

        Assert.Equal(new[] { "second", "third" }, Names(list));
        Assert.Equal(new[] { "1番目", "2番目" }, list.Rows.Select(row => row.Detail).ToArray());
        Assert.Equal(1, list.Tabs[1].Count);
        Assert.Equal(2, list.Tabs[2].Count);
    }

    [Fact]
    public void Waiting_to_retry_explains_why_which_attempt_and_when()
    {
        var (list, source, time) = Create();
        source.Add("photo.jpg", TransferState.WaitingToRetry, failedAttempts: 1,
            failure: TransferFailure.Transient(FailureReason.ConnectionLost), retryAt: Noon + TimeSpan.FromSeconds(8));

        var row = Assert.Single(list.Rows);
        Assert.Equal("再試行待ち", row.StateLabel);
        Assert.Equal("接続が切れたため・2/5回目・8秒後に再開", row.Detail);

        time.Advance(TimeSpan.FromSeconds(5));
        list.Tick();
        Assert.Equal("接続が切れたため・2/5回目・3秒後に再開", row.Detail);

        time.Advance(TimeSpan.FromSeconds(5));
        list.Tick();
        Assert.Equal("接続が切れたため・2/5回目・まもなく再開", row.Detail);
    }

    [Fact]
    public void Finished_rows_show_how_they_ended()
    {
        var (list, source, _) = Create();
        var localNoon = TimeZoneInfo.ConvertTime(Noon, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        source.Add("ok.html", TransferState.Completed, transferred: 1000, sizeVerified: true, finishedAt: Noon);
        source.Add("again.html", TransferState.Completed, transferred: 1000, failedAttempts: 2, sizeVerified: true, finishedAt: Noon);
        source.Add("denied.php", TransferState.Failed, failedAttempts: 1, failure: FtpReplyClassifier.Classify(550, "Permission denied."), finishedAt: Noon);
        source.Add("gave-up.jpg", TransferState.Failed, failedAttempts: 5, failure: TransferFailure.Transient(FailureReason.ConnectionLost), finishedAt: Noon);

        var rows = list.Rows.ToDictionary(row => row.Name);
        Assert.Equal($"サイズ照合OK・{localNoon}", rows["ok.html"].Detail);
        Assert.Equal($"サイズ照合OK・3回目で成功・{localNoon}", rows["again.html"].Detail);
        Assert.Equal("権限がありません（550）", rows["denied.php"].Detail);
        Assert.Equal("接続が切れました・5回試しました", rows["gave-up.jpg"].Detail);
        Assert.True(rows["denied.php"].CanRetry);
        Assert.False(rows["ok.html"].CanCancel);
    }

    [Fact]
    public void Speed_and_remaining_time_come_from_recent_progress()
    {
        var (list, source, time) = Create();
        const long megabyte = 1024 * 1024;
        var item = source.Add("big.zip", TransferState.Running, total: 10 * megabyte, transferred: 0);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Update(item with { TransferredBytes = megabyte });
        time.Advance(TimeSpan.FromSeconds(1));
        source.Update(item with { TransferredBytes = 2 * megabyte });
        list.Tick();

        var row = Assert.Single(list.Rows);
        Assert.Equal("20%", row.PercentText);
        Assert.Equal("1.0 MB/s・残り 8秒", row.Detail);
        Assert.Equal("1.0 MB/s", list.SpeedText);
        Assert.Equal("残り 8秒", list.RemainingText);
    }

    [Fact]
    public void Retry_failed_asks_the_queue_to_send_them_again()
    {
        var (list, source, _) = Create();
        Assert.False(list.RetryFailedCommand.CanExecute(null));

        source.Add("failed", TransferState.Failed, failure: TransferFailure.Permanent(FailureReason.PermissionDenied));
        list.RetryFailedCommand.Execute(null);

        Assert.Equal(1, source.RetryFailedCalls);
    }

    [Fact]
    public void An_empty_queue_says_so()
    {
        var (list, _, _) = Create();

        Assert.False(list.HasItems);
        Assert.False(list.HasRows);
        Assert.Equal("転送待ちはありません", list.IdleText);
        Assert.NotEqual("", list.EmptyText);
        Assert.Equal("0%", list.OverallPercentText);
        Assert.Equal("", list.SpeedText);
    }

    [Fact]
    public void Upload_and_download_rows_show_where_the_file_goes()
    {
        var (list, source, _) = Create();
        source.Add("site/index.html", TransferState.Queued);
        source.Add("logs/access.log", TransferState.Queued, direction: TransferDirection.Download);

        var rows = list.Rows.ToDictionary(row => row.Name);
        Assert.Equal("→ /var/www/html/site", rows["index.html"].Destination);
        Assert.Equal(@"→ C:\Users\user\Sites\logs", rows["access.log"].Destination);
        Assert.True(rows["access.log"].IsDownload);
    }
}
