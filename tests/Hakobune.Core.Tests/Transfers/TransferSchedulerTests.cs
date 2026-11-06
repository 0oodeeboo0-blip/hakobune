using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Transfers;

public class TransferSchedulerTests
{
    // テストでは待ち時間を 0 にして、やり直しの回数だけを確かめる。
    private static TransferOptions FastOptions(int maxConcurrency = 3, int maxAttempts = 10) => new()
    {
        MaxConcurrency = maxConcurrency,
        RetryPolicy = new RetryPolicy(maxAttempts, [TimeSpan.Zero], jitterRatio: 0),
        ConnectPolicy = new RetryPolicy(3, [TimeSpan.Zero], jitterRatio: 0),
    };

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static List<TransferRequest> UploadRequests(TempDirectory temp, int count, Func<int, byte[]> content, ConflictPolicy onConflict = ConflictPolicy.Overwrite)
    {
        var requests = new List<TransferRequest>(count);
        for (var i = 0; i < count; i++)
        {
            var local = temp.CreateFile($"site/dir{i % 20}/file{i}.txt", content(i));
            requests.Add(new TransferRequest(TransferDirection.Upload, local, $"/upload/dir{i % 20}/file{i}.txt", onConflict));
        }
        return requests;
    }

    [Fact]
    public async Task Uploads_all_files_even_when_connections_drop_randomly()
    {
        using var temp = new TempDirectory();
        var random = new Random(20261106);
        var contents = Enumerable.Range(0, 2000).Select(_ => RandomBytes(random, random.Next(0, 4096))).ToArray();
        var requests = UploadRequests(temp, 2000, i => contents[i]);

        var faultRandom = new Random(7);
        var server = new FakeServer
        {
            // 1 割のアップロードで、半分まで送ったところで接続が切れる
            UploadFault = (_, _, length) =>
            {
                lock (faultRandom)
                {
                    return faultRandom.NextDouble() < 0.1
                        ? new WriteFault(length / 2, TransferFailure.Transient(FailureReason.ConnectionLost))
                        : null;
                }
            },
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 4));
        scheduler.Enqueue(requests);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, result.Outcome);
        Assert.Equal(2000, result.Summary.Completed);
        Assert.Equal(0, result.Summary.Failed);
        for (var i = 0; i < requests.Count; i++)
            Assert.Equal(contents[i], server.Files.ReadFile(requests[i].RemotePath));
        Assert.True(server.SessionsCreated > 4, "接続が切れたあと、つなぎ直しているはず");
        Assert.InRange(server.MaxConnectedSessions, 1, 4);
    }

    [Fact]
    public async Task Permanent_failures_are_reported_once_without_retrying()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 50, i => [(byte)i]);
        var server = new FakeServer
        {
            UploadFault = (path, _, _) => path.EndsWith("7.txt", StringComparison.Ordinal)
                ? new WriteFault(0, FtpReplyClassifier.Classify(550, "Permission denied."))
                : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 2));
        scheduler.Enqueue(requests);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, result.Outcome);
        Assert.Equal(5, result.Summary.Failed); // file7, 17, 27, 37, 47
        Assert.Equal(45, result.Summary.Completed);
        Assert.All(scheduler.GetItems().Where(item => item.State == TransferState.Failed), item =>
        {
            Assert.Equal(1, item.FailedAttempts);
            Assert.Equal(FailureReason.PermissionDenied, item.LastFailure?.Reason);
        });

        // 直らない失敗では接続を捨てないので、つなぎ直しは起きない
        Assert.InRange(server.SessionsCreated, 1, 2);
    }

    [Fact]
    public async Task Transient_failures_are_retried_in_place_not_moved_to_the_end()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 10, _ => [1, 2, 3]);
        var failedOnce = 0;
        var server = new FakeServer
        {
            UploadFault = (path, _, _) => path == requests[0].RemotePath && Interlocked.Exchange(ref failedOnce, 1) == 0
                ? new WriteFault(0, TransferFailure.Transient(FailureReason.ConnectionLost))
                : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 1));
        scheduler.Enqueue(requests);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, result.Summary.Completed);
        var order = server.CompletedUploads.ToList();
        Assert.Equal(10, order.Count);
        // FFFTP なら最後に回るところ。Hakobune ではその場でやり直すので最初に届く。
        Assert.Equal(requests[0].RemotePath, order[0]);
    }

    [Fact]
    public async Task Concurrency_drops_to_what_the_server_allows()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 200, i => [(byte)i]);
        var server = new FakeServer { MaxSessions = 2 };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 5));
        scheduler.Enqueue(requests);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, result.Outcome);
        Assert.Equal(200, result.Summary.Completed);
        Assert.InRange(server.MaxConnectedSessions, 1, 2);
        // 接続を断られたことは、項目の失敗には数えない
        Assert.All(scheduler.GetItems(), item => Assert.Equal(0, item.FailedAttempts));
    }

    [Fact]
    public async Task Queue_pauses_without_using_up_attempts_when_the_server_is_unreachable()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 20, _ => [1]);
        var server = new FakeServer
        {
            ConnectFault = () => TransferFailure.Transient(FailureReason.ConnectionLost, "接続できません"),
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 3));
        scheduler.Enqueue(requests);

        var paused = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Paused, paused.Outcome);
        Assert.Equal(FailureReason.ConnectionLost, paused.PauseReason?.Reason);
        Assert.Equal(20, paused.Summary.Queued);
        Assert.All(scheduler.GetItems(), item => Assert.Equal(0, item.FailedAttempts));

        // サーバーが戻ったら、続きから全部送れる
        server.ConnectFault = null;
        var resumed = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, resumed.Outcome);
        Assert.Equal(20, resumed.Summary.Completed);
    }

    [Fact]
    public async Task Login_failure_pauses_immediately()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 5, _ => [1]);
        var server = new FakeServer
        {
            ConnectFault = () => FtpReplyClassifier.Classify(530, "Login incorrect."),
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 3));
        scheduler.Enqueue(requests);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Paused, result.Outcome);
        Assert.Equal(FailureReason.AuthenticationFailed, result.PauseReason?.Reason);
        Assert.Equal(5, result.Summary.Queued);
        // パスワードの間違いは、何度やっても同じなので繰り返さない
        Assert.InRange(server.ConnectAttempts, 1, 3);
    }

    [Fact]
    public async Task Interrupted_upload_resumes_from_where_it_stopped()
    {
        using var temp = new TempDirectory();
        var content = RandomBytes(new Random(1), 1_000_000);
        var local = temp.CreateFile("big.bin", content);
        var attempts = 0;
        var server = new FakeServer
        {
            UploadFault = (_, _, _) => Interlocked.Increment(ref attempts) == 1
                ? new WriteFault(400_000, TransferFailure.Transient(FailureReason.ConnectionLost))
                : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 1));
        scheduler.Enqueue([new TransferRequest(TransferDirection.Upload, local, "/up/big.bin")]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Completed);
        Assert.Equal(new long[] { 0, 400_000 }, server.Uploads.Select(upload => upload.Offset).ToArray());
        Assert.Equal(content, server.Files.ReadFile("/up/big.bin"));
    }

    [Fact]
    public async Task Retry_does_not_mistake_its_own_partial_file_for_a_conflict()
    {
        using var temp = new TempDirectory();
        var content = RandomBytes(new Random(2), 10_000);
        var attempts = 0;
        var server = new FakeServer
        {
            UploadFault = (_, _, _) => Interlocked.Increment(ref attempts) == 1
                ? new WriteFault(3_000, TransferFailure.Transient(FailureReason.ConnectionLost))
                : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 1));
        scheduler.Enqueue([new TransferRequest(TransferDirection.Upload, temp.CreateFile("a.bin", content), "/up/a.bin", ConflictPolicy.Skip)]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Completed);
        Assert.Equal(0, result.Summary.Skipped);
        Assert.Equal(content, server.Files.ReadFile("/up/a.bin"));
    }

    [Fact]
    public async Task Size_mismatch_after_upload_is_detected_and_retried()
    {
        using var temp = new TempDirectory();
        var content = RandomBytes(new Random(3), 5_000);
        var attempts = 0;
        var server = new FakeServer
        {
            // 1 回目は、エラーを出さずに途中までしか書かれない
            UploadFault = (_, _, _) => Interlocked.Increment(ref attempts) == 1 ? new WriteFault(1_000, null) : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 1));
        scheduler.Enqueue([new TransferRequest(TransferDirection.Upload, temp.CreateFile("a.bin", content), "/up/a.bin")]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Completed);
        var item = Assert.Single(scheduler.GetItems());
        Assert.Equal(1, item.FailedAttempts);
        Assert.Equal(FailureReason.VerificationFailed, item.LastFailure?.Reason);
        Assert.Equal(content, server.Files.ReadFile("/up/a.bin"));
    }

    [Fact]
    public async Task Skip_policy_keeps_the_existing_remote_file()
    {
        using var temp = new TempDirectory();
        var server = new FakeServer();
        server.Files.CreateDirectory("/up");
        server.Files.WriteFile("/up/a.txt", new byte[] { 9, 9, 9 }, 0, DateTimeOffset.UtcNow);
        var scheduler = new TransferScheduler(server, FastOptions());
        scheduler.Enqueue([new TransferRequest(TransferDirection.Upload, temp.CreateFile("a.txt", [1, 2, 3]), "/up/a.txt", ConflictPolicy.Skip)]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Skipped);
        Assert.Equal(new byte[] { 9, 9, 9 }, server.Files.ReadFile("/up/a.txt"));
    }

    [Fact]
    public async Task Overwrite_if_newer_compares_modified_times()
    {
        using var temp = new TempDirectory();
        var server = new FakeServer();
        server.Files.CreateDirectory("/up");
        var remoteTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        server.Files.WriteFile("/up/old.txt", new byte[] { 7 }, 0, remoteTime);
        server.Files.WriteFile("/up/new.txt", new byte[] { 7 }, 0, remoteTime);

        var older = temp.CreateFile("old.txt", [1]);
        File.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = temp.CreateFile("new.txt", [2]);
        File.SetLastWriteTimeUtc(newer, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var scheduler = new TransferScheduler(server, FastOptions());
        scheduler.Enqueue(
        [
            new TransferRequest(TransferDirection.Upload, older, "/up/old.txt", ConflictPolicy.OverwriteIfNewer),
            new TransferRequest(TransferDirection.Upload, newer, "/up/new.txt", ConflictPolicy.OverwriteIfNewer),
        ]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Skipped);
        Assert.Equal(1, result.Summary.Completed);
        Assert.Equal(new byte[] { 7 }, server.Files.ReadFile("/up/old.txt"));
        Assert.Equal(new byte[] { 2 }, server.Files.ReadFile("/up/new.txt"));
    }

    [Fact]
    public async Task Failed_items_can_be_sent_again()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 10, _ => [1]);
        var server = new FakeServer
        {
            UploadFault = (path, _, _) => path == requests[3].RemotePath
                ? new WriteFault(0, FtpReplyClassifier.Classify(553, "Could not create file."))
                : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions());
        scheduler.Enqueue(requests);

        var first = await scheduler.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Summary.Failed);

        // サーバー側の問題を直してから「失敗分を再送」
        server.UploadFault = null;
        Assert.Equal(1, scheduler.RetryFailed());
        var second = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, second.Outcome);
        Assert.Equal(10, second.Summary.Completed);
    }

    [Fact]
    public async Task Downloads_all_files_even_when_connections_drop_randomly()
    {
        using var temp = new TempDirectory();
        var server = new FakeServer();
        server.Files.CreateDirectory("/data");
        var random = new Random(4);
        var expected = new Dictionary<string, byte[]>();
        var requests = new List<TransferRequest>();
        for (var i = 0; i < 500; i++)
        {
            var content = RandomBytes(random, random.Next(1, 4096));
            server.Files.WriteFile($"/data/file{i}.bin", content, 0, DateTimeOffset.UtcNow);
            var local = Path.Combine(temp.Path, "down", $"file{i}.bin");
            expected[local] = content;
            requests.Add(new TransferRequest(TransferDirection.Download, local, $"/data/file{i}.bin"));
        }

        var faultRandom = new Random(11);
        server.DownloadFault = (_, _, length) =>
        {
            lock (faultRandom)
            {
                return faultRandom.NextDouble() < 0.1
                    ? new WriteFault(length / 2, TransferFailure.Transient(FailureReason.ConnectionLost))
                    : null;
            }
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 3));
        scheduler.Enqueue(requests);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferRunOutcome.Finished, result.Outcome);
        Assert.Equal(500, result.Summary.Completed);
        foreach (var (local, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(local));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "down"), "*.hakobune-part"));
    }

    [Fact]
    public async Task Failed_download_leaves_the_existing_local_file_untouched()
    {
        using var temp = new TempDirectory();
        var local = temp.CreateFile("a.txt", [1, 1, 1]);
        var server = new FakeServer();
        server.Files.CreateDirectory("/d");
        server.Files.WriteFile("/d/a.txt", new byte[] { 5, 5, 5, 5, 5, 5, 5, 5 }, 0, DateTimeOffset.UtcNow);

        // 毎回、途中で接続が切れる
        server.DownloadFault = (_, _, length) => new WriteFault(length / 2, TransferFailure.Transient(FailureReason.ConnectionLost));
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 1, maxAttempts: 3));
        scheduler.Enqueue([new TransferRequest(TransferDirection.Download, local, "/d/a.txt")]);

        var result = await scheduler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.Failed);
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(local));
    }

    [Fact]
    public async Task Cancelling_returns_running_items_to_the_queue()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 5, _ => [1]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new FakeServer
        {
            BeforeUpload = async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            },
        };
        var scheduler = new TransferScheduler(server, FastOptions(maxConcurrency: 1));
        scheduler.Enqueue(requests);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = scheduler.RunAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var result = await run;

        Assert.Equal(TransferRunOutcome.Cancelled, result.Outcome);
        Assert.Equal(5, result.Summary.Queued);
    }

    [Fact]
    public async Task Finished_items_record_when_they_finished_and_whether_the_size_was_checked()
    {
        using var temp = new TempDirectory();
        var requests = UploadRequests(temp, 3, _ => [1, 2, 3]);
        var server = new FakeServer
        {
            UploadFault = (path, _, _) => path == requests[2].RemotePath
                ? new WriteFault(0, FtpReplyClassifier.Classify(550, "Permission denied."))
                : null,
        };
        var scheduler = new TransferScheduler(server, FastOptions());
        scheduler.Enqueue(requests);
        var before = DateTimeOffset.UtcNow;

        await scheduler.RunAsync(TestContext.Current.CancellationToken);

        var items = scheduler.GetItems();
        Assert.All(items, item => Assert.InRange(item.FinishedAt ?? DateTimeOffset.MinValue, before, DateTimeOffset.UtcNow));
        Assert.True(items[0].SizeVerified);
        Assert.True(items[1].SizeVerified);
        Assert.Equal(TransferState.Failed, items[2].State);
        Assert.False(items[2].SizeVerified);

        // 送り直すときは、終わった時刻を消す
        scheduler.RetryFailed();
        Assert.Null(scheduler.GetItems()[2].FinishedAt);
    }

    [Fact]
    public void Added_and_restored_items_are_announced_so_the_screen_can_show_them()
    {
        using var temp = new TempDirectory();
        var scheduler = new TransferScheduler(new FakeServer(), FastOptions());
        var announced = new List<TransferItemSnapshot>();
        scheduler.ItemChanged += (_, snapshot) => announced.Add(snapshot);

        scheduler.Enqueue(UploadRequests(temp, 3, _ => [1]));
        scheduler.Restore(scheduler.SaveState().Select(item => item with { Id = Guid.NewGuid() }).ToList());

        Assert.Equal(6, announced.Count);
        Assert.All(announced, snapshot => Assert.Equal(TransferState.Queued, snapshot.State));
        Assert.Equal(6, announced.Select(snapshot => snapshot.Id).Distinct().Count());
    }

    [Fact]
    public async Task Size_is_not_reported_as_checked_when_checking_is_turned_off()
    {
        using var temp = new TempDirectory();
        var scheduler = new TransferScheduler(new FakeServer(), FastOptions() with { VerifySize = false });
        scheduler.Enqueue(UploadRequests(temp, 1, _ => [1]));

        await scheduler.RunAsync(TestContext.Current.CancellationToken);

        var item = Assert.Single(scheduler.GetItems());
        Assert.Equal(TransferState.Completed, item.State);
        Assert.False(item.SizeVerified);
    }
}
