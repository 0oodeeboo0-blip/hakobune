using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Transfers;

public class TransferQueueFileTests
{
    private static TransferOptions FastOptions() => new()
    {
        MaxConcurrency = 1,
        RetryPolicy = new RetryPolicy(10, [TimeSpan.Zero], jitterRatio: 0),
        ConnectPolicy = new RetryPolicy(3, [TimeSpan.Zero], jitterRatio: 0),
    };

    [Fact]
    public async Task The_queue_continues_after_the_app_is_closed_in_the_middle()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var requests = Enumerable.Range(0, 20)
            .Select(i => new TransferRequest(TransferDirection.Upload, temp.CreateFile($"f{i}.txt", [(byte)i]), $"/up/f{i}.txt"))
            .ToList();
        var server = new FakeServer();

        // 6 件目のアップロードの途中でアプリが閉じられた
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var uploads = 0;
        server.BeforeUpload = async token =>
        {
            if (Interlocked.Increment(ref uploads) == 6)
            {
                await closing.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var first = new TransferScheduler(server, FastOptions());
        first.Enqueue(requests);
        var stopped = await first.RunAsync(closing.Token);
        Assert.Equal(TransferRunOutcome.Cancelled, stopped.Outcome);
        Assert.Equal(5, stopped.Summary.Completed);

        var file = Path.Combine(temp.Path, "queue.json");
        await TransferQueueFile.SaveAsync(file, first.SaveState(), cancellationToken);

        // 次に起動したとき、続きから送る
        server.BeforeUpload = null;
        var second = new TransferScheduler(server, FastOptions());
        second.Restore(await TransferQueueFile.LoadAsync(file, cancellationToken));
        var finished = await second.RunAsync(cancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, finished.Outcome);
        Assert.Equal(20, finished.Summary.Completed);
        // 先に終わっていた 5 件は送り直さない
        for (var i = 0; i < 5; i++)
            Assert.Single(server.Uploads, upload => upload.Path == $"/up/f{i}.txt");
        Assert.Equal(first.SaveState().Select(item => item.Id), second.SaveState().Select(item => item.Id));
    }

    [Fact]
    public async Task Saved_items_keep_their_failure_and_progress()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var saved = new SavedTransferItem(
            Guid.NewGuid(), TransferDirection.Download, @"C:\Users\user\日本語.txt", "/data/日本語.txt", ConflictPolicy.OverwriteIfNewer,
            TransferState.Failed, 3, FailureReason.PermissionDenied, FailureKind.Permanent, 550, "Permission denied.",
            HasWrittenTarget: true, TotalBytes: 1000, TransferredBytes: 400, Note: null);
        var file = Path.Combine(temp.Path, "nested", "queue.json");

        await TransferQueueFile.SaveAsync(file, [saved], cancellationToken);
        var loaded = await TransferQueueFile.LoadAsync(file, cancellationToken);

        Assert.Equal(saved, Assert.Single(loaded));
    }

    [Fact]
    public async Task A_missing_file_means_an_empty_queue()
    {
        using var temp = new TempDirectory();

        var loaded = await TransferQueueFile.LoadAsync(Path.Combine(temp.Path, "none.json"), TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task A_broken_file_is_set_aside_instead_of_crashing()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("queue.json", "{ これは壊れている"u8.ToArray());

        var loaded = await TransferQueueFile.LoadAsync(file, TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".broken"));
    }
}
