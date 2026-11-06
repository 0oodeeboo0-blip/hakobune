using Hakobune.Core.Browsing;
using Hakobune.Core.Remote;
using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Browsing;

public class RemoteBrowserTests
{
    private static FakeServer ServerWithFiles()
    {
        var server = new FakeServer();
        server.Files.CreateDirectory("/data");
        server.Files.WriteFile("/data/a.txt", new byte[] { 1 }, 0, DateTimeOffset.UtcNow);
        server.Files.WriteFile("/data/b.txt", new byte[] { 2 }, 0, DateTimeOffset.UtcNow);
        server.Files.CreateDirectory("/data/sub");
        return server;
    }

    [Fact]
    public async Task A_failed_refresh_keeps_showing_the_previous_listing()
    {
        var server = ServerWithFiles();
        await using var browser = new RemoteBrowser(server);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await browser.ListAsync("/data", cancellationToken);
        Assert.True(first.Succeeded);
        Assert.Equal(3, first.Items.Count);

        // サーバーとのやり取りが失敗し続ける
        server.ListFault = _ => TransferFailure.Transient(FailureReason.ConnectionLost);
        var failed = await browser.ListAsync("/data", cancellationToken);

        // FFFTP のように一覧を消さず、前回の一覧を「最新ではない」として返す
        Assert.False(failed.Succeeded);
        Assert.True(failed.IsStale);
        Assert.Equal(FailureReason.ConnectionLost, failed.Failure?.Reason);
        Assert.Equal(3, failed.Items.Count);

        // 直れば、最新の一覧に戻る
        server.ListFault = null;
        var recovered = await browser.ListAsync("/data", cancellationToken);
        Assert.True(recovered.Succeeded);
        Assert.False(recovered.IsStale);
    }

    [Fact]
    public async Task A_dropped_connection_is_replaced_without_the_user_noticing()
    {
        var server = ServerWithFiles();
        await using var browser = new RemoteBrowser(server);
        var cancellationToken = TestContext.Current.CancellationToken;
        await browser.ListAsync("/data", cancellationToken);

        var failures = 0;
        server.ListFault = _ => Interlocked.Increment(ref failures) == 1
            ? TransferFailure.Transient(FailureReason.ConnectionLost)
            : null;
        var result = await browser.ListAsync("/data", cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, server.SessionsCreated); // 切れた接続を捨てて、つなぎ直した
    }

    [Fact]
    public async Task A_permanent_failure_is_reported_without_retrying()
    {
        var server = ServerWithFiles();
        server.ListFault = _ => FtpReplyClassifier.Classify(550, "Permission denied.");
        await using var browser = new RemoteBrowser(server);

        var result = await browser.ListAsync("/data", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.IsStale);
        Assert.Empty(result.Items);
        Assert.Equal(FailureReason.PermissionDenied, result.Failure?.Reason);
        Assert.Equal(1, server.SessionsCreated);
    }

    [Fact]
    public async Task Requests_at_the_same_time_never_share_the_connection_at_once()
    {
        var server = ServerWithFiles();
        server.BeforeList = cancellationToken => Task.Delay(5, cancellationToken);
        await using var browser = new RemoteBrowser(server);
        var cancellationToken = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => browser.ListAsync("/data", cancellationToken)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.False(server.ConcurrentUseDetected, "1 本の接続が同時に使われた");
        Assert.Equal(1, server.SessionsCreated);
    }

    [Fact]
    public async Task Keep_alive_drops_a_dead_connection_and_the_next_request_reconnects()
    {
        var server = ServerWithFiles();
        await using var browser = new RemoteBrowser(server);
        var cancellationToken = TestContext.Current.CancellationToken;
        await browser.ListAsync("/data", cancellationToken);

        server.KeepAliveFault = () => TransferFailure.Transient(FailureReason.ConnectionLost);
        Assert.False(await browser.KeepAliveAsync(cancellationToken));

        server.KeepAliveFault = null;
        var result = await browser.ListAsync("/data", cancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(2, server.SessionsCreated);
    }

    [Fact]
    public async Task Other_operations_report_failures_and_reconnect_next_time()
    {
        var server = ServerWithFiles();
        await using var browser = new RemoteBrowser(server);
        var cancellationToken = TestContext.Current.CancellationToken;

        await browser.CreateDirectoryAsync("/data/new", cancellationToken);
        await browser.RenameAsync("/data/a.txt", "/data/c.txt", cancellationToken);
        await browser.DeleteFileAsync("/data/b.txt", cancellationToken);
        var missing = await Assert.ThrowsAsync<RemoteOperationException>(() => browser.DeleteFileAsync("/data/missing.txt", cancellationToken));
        Assert.Equal(FailureReason.NotFound, missing.Failure.Reason);

        var listing = await browser.ListAsync("/data", cancellationToken);
        Assert.Equal(new[] { "c.txt", "new", "sub" }, listing.Items.Select(item => item.Name).Order().ToArray());
        Assert.Equal("/", await browser.GetHomeDirectoryAsync(cancellationToken));
    }
}
