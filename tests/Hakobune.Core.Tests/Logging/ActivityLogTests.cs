using Hakobune.Core.Logging;

namespace Hakobune.Core.Tests.Logging;

public class ActivityLogTests
{
    [Fact]
    public void Entries_are_kept_in_order_with_their_kind_and_source()
    {
        var log = new ActivityLog();

        log.Info("接続しています", "ショップ");
        log.Success("接続しました", "ショップ");
        log.Detail("Command:  PWD");

        var snapshot = log.GetSnapshot();
        Assert.Equal(new[] { "接続しています", "接続しました", "Command:  PWD" }, snapshot.Entries.Select(entry => entry.Message).ToArray());
        Assert.Equal(new[] { LogKind.Info, LogKind.Success, LogKind.Detail }, snapshot.Entries.Select(entry => entry.Kind).ToArray());
        Assert.Equal(new long[] { 1, 2, 3 }, snapshot.Entries.Select(entry => entry.Sequence).ToArray());
        Assert.Equal("ショップ", snapshot.Entries[0].Source);
        Assert.Null(snapshot.Entries[2].Source);
        Assert.Equal(3, snapshot.LastSequence);
    }

    [Fact]
    public void Only_the_newest_entries_are_kept()
    {
        var log = new ActivityLog(capacity: 3);

        for (var i = 1; i <= 5; i++)
            log.Info($"{i} 行目");

        var snapshot = log.GetSnapshot();
        Assert.Equal(new[] { "3 行目", "4 行目", "5 行目" }, snapshot.Entries.Select(entry => entry.Message).ToArray());
        Assert.Equal(5, snapshot.LastSequence);
    }

    [Fact]
    public void Clearing_keeps_counting_from_where_it_was()
    {
        var log = new ActivityLog();
        var cleared = 0;
        log.Cleared += (_, _) => cleared++;
        log.Info("a");
        log.Info("b");

        log.Clear();
        var after = log.Info("c");

        Assert.Equal(1, cleared);
        Assert.Equal(3, after.Sequence);
        Assert.Equal(new[] { "c" }, log.GetSnapshot().Entries.Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void Several_lines_become_one()
    {
        var log = new ActivityLog();

        var entry = log.Detail("Response: 211-Features:\r\n MDTM\r\n SIZE\r\n211 End\r\n");

        Assert.Equal("Response: 211-Features: MDTM SIZE 211 End", entry.Message);
    }

    [Fact]
    public void Writers_are_told_about_each_entry()
    {
        var log = new ActivityLog();
        var added = new List<LogEntry>();
        log.Added += (_, entry) => added.Add(entry);

        var entry = log.Error("送れませんでした");

        Assert.Same(entry, Assert.Single(added));
    }

    [Fact]
    public async Task Many_threads_can_write_at_once()
    {
        var log = new ActivityLog(capacity: 1000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(
            () =>
            {
                for (var i = 0; i < 500; i++)
                    log.Detail($"{worker}:{i}");
            },
            TestContext.Current.CancellationToken)));

        var snapshot = log.GetSnapshot();
        Assert.Equal(1000, snapshot.Entries.Count);
        Assert.Equal(4000, snapshot.LastSequence);
        Assert.Equal(snapshot.Entries.Select(entry => entry.Sequence).Order(), snapshot.Entries.Select(entry => entry.Sequence));
        Assert.Equal(4000, snapshot.Entries[^1].Sequence);
    }
}
