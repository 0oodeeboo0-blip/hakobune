using System.Globalization;
using Hakobune.Core.Logging;
using Hakobune.Presentation.Logging;

namespace Hakobune.Presentation.Tests.Logging;

public sealed class LogViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 8, 16, 3, TimeSpan.Zero);

    [Fact]
    public void New_entries_appear_at_the_bottom_and_details_only_when_asked()
    {
        var time = new ManualTime(Start);
        var log = new ActivityLog(timeProvider: time);
        using var view = new LogViewModel(log, time);
        var appended = 0;
        view.RowsAppended += (_, _) => appended++;

        log.Info("ショップ に接続しています", "ショップ");
        log.Detail("Command:  USER web", "ショップ");
        log.Success("接続しました（SFTP・暗号化）", "ショップ");

        Assert.Equal(new[] { "ショップ に接続しています", "接続しました（SFTP・暗号化）" }, view.Rows.Select(row => row.Message).ToArray());
        Assert.Equal(2, appended);
        Assert.True(view.Rows[1].IsSuccess);
        Assert.False(view.IsEmpty);

        view.ShowsDetails = true;

        Assert.Equal(3, view.Rows.Count);
        Assert.True(view.Rows[1].IsDetail);
        Assert.Equal("Command:  USER web", view.Rows[1].Message);
    }

    [Fact]
    public void Entries_written_before_the_view_are_shown()
    {
        var log = new ActivityLog();
        log.Warning("一覧を更新できませんでした");

        using var view = new LogViewModel(log);

        var row = Assert.Single(view.Rows);
        Assert.True(row.IsWarning);
    }

    [Fact]
    public void Clearing_empties_the_view_and_the_log()
    {
        var log = new ActivityLog();
        using var view = new LogViewModel(log);
        Assert.True(view.IsEmpty);
        Assert.False(view.ClearCommand.CanExecute(null));
        log.Info("a");
        log.Info("b");
        Assert.True(view.ClearCommand.CanExecute(null));

        view.ClearCommand.Execute(null);

        Assert.Empty(view.Rows);
        Assert.True(view.IsEmpty);
        Assert.Empty(log.GetSnapshot().Entries);
        Assert.False(view.ClearCommand.CanExecute(null));

        log.Info("c");
        Assert.Equal("c", Assert.Single(view.Rows).Message);
    }

    [Fact]
    public void Old_rows_go_away_with_the_log()
    {
        var log = new ActivityLog(capacity: 3);
        using var view = new LogViewModel(log);

        for (var i = 1; i <= 5; i++)
            log.Info($"{i} 行目");

        Assert.Equal(new[] { "3 行目", "4 行目", "5 行目" }, view.Rows.Select(row => row.Message).ToArray());
    }

    [Fact]
    public void Copied_text_has_the_time_kind_and_server()
    {
        var time = new ManualTime(Start);
        var log = new ActivityLog(timeProvider: time);
        using var view = new LogViewModel(log, time);
        log.Error("送れませんでした: /var/www/config.php: 権限がありません（550）", "ショップ");
        time.Advance(TimeSpan.FromSeconds(2));
        log.Info("切断しました");

        var local = TimeZoneInfo.ConvertTime(Start, time.LocalTimeZone);
        var first = local.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        var second = local.AddSeconds(2).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(
            $"{first}  失敗    [ショップ]  送れませんでした: /var/www/config.php: 権限がありません（550）{Environment.NewLine}" +
            $"{second}  情報    切断しました{Environment.NewLine}",
            view.ToText());
        Assert.Equal(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), view.Rows[0].TimeText);
        Assert.StartsWith("失敗、", view.Rows[0].AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_from_many_threads_all_arrive()
    {
        var log = new ActivityLog();
        using var view = new LogViewModel(log);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(
            () =>
            {
                for (var i = 0; i < 250; i++)
                    log.Info($"{worker}:{i}");
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(1000, view.Rows.Count);
        Assert.Equal(1000, view.Rows.Select(row => row.Message).Distinct().Count());
    }
}
