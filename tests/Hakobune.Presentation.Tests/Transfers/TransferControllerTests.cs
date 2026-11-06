using Hakobune.Core.Transfers;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Transfers;

public sealed class TransferControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));

    public TransferControllerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static TransferOptions Fast => new()
    {
        MaxConcurrency = 2,
        RetryPolicy = new RetryPolicy(3, [TimeSpan.Zero], jitterRatio: 0),
        ConnectPolicy = new RetryPolicy(2, [TimeSpan.Zero], jitterRatio: 0),
    };

    private string QueueFile => Path.Combine(_folder, "queue.json");

    private List<TransferRequest> Uploads(int count)
    {
        var requests = new List<TransferRequest>();
        for (var i = 0; i < count; i++)
        {
            var local = Path.Combine(_folder, $"file{i}.txt");
            File.WriteAllBytes(local, new byte[10 + i]);
            requests.Add(new TransferRequest(TransferDirection.Upload, local, $"/www/file{i}.txt", ExpectedBytes: 10 + i));
        }
        return requests;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("待っていた状態になりませんでした");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Added_files_are_sent_right_away_and_then_it_stops()
    {
        var server = new MemoryTransferServer();
        await using var controller = new TransferController(server, Fast, QueueFile);

        controller.Enqueue(Uploads(5));
        await WaitForAsync(() => controller.GetItems().All(item => item.State == TransferState.Completed) && !controller.IsRunning);

        Assert.Equal(5, server.Files.Count);
        Assert.Equal(14, server.Files["/www/file4.txt"].Length);
        Assert.False(controller.IsPaused);
        Assert.Equal(0, controller.PendingCount);
    }

    [Fact]
    public async Task Pausing_keeps_the_rest_and_resuming_continues()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new MemoryTransferServer { BeforeUpload = (_, token) => release.Task.WaitAsync(token) };
        await using var controller = new TransferController(server, Fast, QueueFile);
        controller.Enqueue(Uploads(4));
        await WaitForAsync(() => controller.GetItems().Any(item => item.State == TransferState.Running));

        controller.Pause();
        await WaitForAsync(() => !controller.IsRunning);

        Assert.True(controller.IsPaused);
        Assert.Null(controller.StopReason);
        Assert.Empty(server.Files);
        Assert.All(controller.GetItems(), item => Assert.Equal(TransferState.Queued, item.State));

        release.SetResult();
        controller.Resume();
        await WaitForAsync(() => controller.GetItems().All(item => item.State == TransferState.Completed));
        Assert.Equal(4, server.Files.Count);
    }

    [Fact]
    public async Task What_was_left_is_restored_paused_next_time()
    {
        var never = new TaskCompletionSource();
        var server = new MemoryTransferServer { BeforeUpload = (_, token) => never.Task.WaitAsync(token) };
        await using (var first = new TransferController(server, Fast, QueueFile))
        {
            first.Enqueue(Uploads(3));
            await WaitForAsync(() => first.GetItems().Any(item => item.State == TransferState.Running));
        }
        Assert.True(File.Exists(QueueFile));

        server.BeforeUpload = null;
        await using var second = new TransferController(server, Fast, QueueFile);
        await second.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, second.RestoredPendingCount);
        Assert.True(second.IsPaused);
        Assert.False(second.IsRunning);

        second.Resume();
        await WaitForAsync(() => second.GetItems().All(item => item.State == TransferState.Completed) && !second.IsRunning);
        Assert.Equal(3, server.Files.Count);
    }

    [Fact]
    public async Task A_server_that_refuses_the_login_stops_the_queue_with_the_reason()
    {
        var server = new MemoryTransferServer { ConnectFault = TransferFailure.Permanent(FailureReason.AuthenticationFailed) };
        await using var controller = new TransferController(server, Fast);

        controller.Enqueue(Uploads(2));
        await WaitForAsync(() => controller.IsPaused && !controller.IsRunning);

        Assert.Equal(FailureReason.AuthenticationFailed, controller.StopReason?.Reason);
        Assert.All(controller.GetItems(), item => Assert.Equal(TransferState.Queued, item.State));

        server.ConnectFault = null;
        controller.Resume();
        await WaitForAsync(() => controller.GetItems().All(item => item.State == TransferState.Completed));
        Assert.Null(controller.StopReason);
    }

    [Fact]
    public async Task Clearing_finished_items_empties_the_saved_queue()
    {
        var server = new MemoryTransferServer();
        await using var controller = new TransferController(server, Fast, QueueFile);
        controller.Enqueue(Uploads(2));
        await WaitForAsync(() => controller.GetItems().All(item => item.State == TransferState.Completed) && !controller.IsRunning);

        Assert.Equal(2, controller.RemoveFinished());
        await controller.SaveAsync();

        Assert.Empty(controller.GetItems());
        Assert.False(File.Exists(QueueFile));
    }
}
