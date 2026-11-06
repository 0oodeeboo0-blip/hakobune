using System.Text;
using Hakobune.Core.Remote;
using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Integration;

/// <summary>
/// 本物のサーバー（vsftpd、Pure-FTPd、ProFTPD、OpenSSH）を相手にしたテスト。
/// CI では tests/servers/start.sh でサーバーを立ててから実行する。
/// </summary>
[Trait("Category", "Integration")]
public class RemoteIntegrationTests
{
    public static TheoryData<string> Servers => new() { "VSFTPD", "VSFTPD_FTPES", "PUREFTPD", "PROFTPD", "SFTP" };

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Basic_operations_work_on_a_real_server(string name)
    {
        var server = TestServers.Find(name);
        if (server is null)
        {
            Assert.Skip(TestServers.SkipReason(name));
            return;
        }
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var session = server.Factory.Create();
        await session.ConnectAsync(cancellationToken);
        var directory = await TestServers.CreateTestDirectoryAsync(session, cancellationToken);

        var files = new Dictionary<string, byte[]>
        {
            ["a.txt"] = "hello"u8.ToArray(),
            ["空白 と 日本語.txt"] = Encoding.UTF8.GetBytes("日本語の中身"),
            ["empty.bin"] = [],
            ["big.bin"] = TestServers.RandomBytes(1_000_000, seed: 1),
        };
        foreach (var (fileName, content) in files)
        {
            using var source = new MemoryStream(content);
            await session.UploadAsync(source, RemotePath.Combine(directory, fileName), 0, null, cancellationToken);
        }

        var listing = await session.ListAsync(directory, cancellationToken);
        foreach (var (fileName, content) in files)
        {
            var entry = Assert.Single(listing, item => item.Name == fileName);
            Assert.Equal(RemoteItemType.File, entry.Type);
            Assert.Equal(content.Length, entry.Size);
        }

        var big = await session.GetItemAsync(RemotePath.Combine(directory, "big.bin"), cancellationToken);
        Assert.NotNull(big);
        Assert.Equal(1_000_000, big.Size);
        Assert.Null(await session.GetItemAsync(RemotePath.Combine(directory, "missing.txt"), cancellationToken));

        foreach (var (fileName, content) in files)
        {
            using var destination = new MemoryStream();
            await session.DownloadAsync(RemotePath.Combine(directory, fileName), destination, 0, null, cancellationToken);
            Assert.Equal(content, destination.ToArray());
        }

        await session.RenameAsync(RemotePath.Combine(directory, "a.txt"), RemotePath.Combine(directory, "b.txt"), cancellationToken);
        Assert.Null(await session.GetItemAsync(RemotePath.Combine(directory, "a.txt"), cancellationToken));
        Assert.NotNull(await session.GetItemAsync(RemotePath.Combine(directory, "b.txt"), cancellationToken));
        await session.DeleteFileAsync(RemotePath.Combine(directory, "b.txt"), cancellationToken);
        Assert.Null(await session.GetItemAsync(RemotePath.Combine(directory, "b.txt"), cancellationToken));

        await session.CreateDirectoryAsync(RemotePath.Combine(directory, "sub/深い/フォルダ"), cancellationToken);
        var nested = await session.ListAsync(RemotePath.Combine(directory, "sub/深い"), cancellationToken);
        Assert.Contains(nested, item => item.Name == "フォルダ" && item.IsDirectory);

        // 属性を変える
        await session.SetPermissionsAsync(RemotePath.Combine(directory, "big.bin"), Convert.ToInt32("600", 8), cancellationToken);
        // どのサーバーも（MLSD で一覧を返すものも）同じ「-rw-------」の形で属性が出る
        var changed = await session.ListAsync(directory, cancellationToken);
        Assert.Equal("-rw-------", Assert.Single(changed, item => item.Name == "big.bin").Permissions);
        Assert.StartsWith("d", Assert.Single(changed, item => item.IsDirectory).Permissions, StringComparison.Ordinal);

        // フォルダの名前を変え、中身ごと消す
        using (var inside = new MemoryStream("x"u8.ToArray()))
            await session.UploadAsync(inside, RemotePath.Combine(directory, "sub/深い/フォルダ/x.txt"), 0, null, cancellationToken);
        await session.RenameAsync(RemotePath.Combine(directory, "sub"), RemotePath.Combine(directory, "移した"), cancellationToken);
        Assert.NotNull(await session.GetItemAsync(RemotePath.Combine(directory, "移した/深い/フォルダ/x.txt"), cancellationToken));
        await session.DeleteDirectoryAsync(RemotePath.Combine(directory, "移した"), cancellationToken);
        Assert.DoesNotContain(await session.ListAsync(directory, cancellationToken), item => item.Name == "移した");

        await session.KeepAliveAsync(cancellationToken);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Uploads_and_downloads_can_continue_from_an_offset(string name)
    {
        var server = TestServers.Find(name);
        if (server is null)
        {
            Assert.Skip(TestServers.SkipReason(name));
            return;
        }
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var session = server.Factory.Create();
        await session.ConnectAsync(cancellationToken);
        var directory = await TestServers.CreateTestDirectoryAsync(session, cancellationToken);
        var path = RemotePath.Combine(directory, "resume.bin");
        var content = TestServers.RandomBytes(300_000, seed: 2);

        using (var first = new MemoryStream(content, 0, 100_000))
            await session.UploadAsync(first, path, 0, null, cancellationToken);
        using (var rest = new MemoryStream(content, 100_000, 200_000))
            await session.UploadAsync(rest, path, 100_000, null, cancellationToken);

        using var whole = new MemoryStream();
        await session.DownloadAsync(path, whole, 0, null, cancellationToken);
        Assert.Equal(content, whole.ToArray());

        Assert.True(session.Capabilities.CanResumeDownload, "このサーバーは途中からのダウンロードに対応しているはず");
        using var tail = new MemoryStream();
        await session.DownloadAsync(path, tail, 123_456, null, cancellationToken);
        Assert.Equal(content[123_456..], tail.ToArray());
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Every_file_arrives_even_when_the_connection_keeps_dropping(string name)
    {
        var server = TestServers.Find(name);
        if (server is null)
        {
            Assert.Skip(TestServers.SkipReason(name));
            return;
        }
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();

        string directory;
        await using (var setup = server.Factory.Create())
        {
            await setup.ConnectAsync(cancellationToken);
            directory = await TestServers.CreateTestDirectoryAsync(setup, cancellationToken);
        }

        var random = new Random(3);
        var contents = new Dictionary<string, byte[]>();
        var requests = new List<TransferRequest>();
        for (var i = 0; i < 400; i++)
        {
            var content = TestServers.RandomBytes(random.Next(0, 64 * 1024), random);
            var relative = $"d{i % 8}/{(i % 5 == 0 ? "日本語" : "file")}{i}.bin";
            var local = temp.CreateFile(Path.Combine("up", relative), content);
            var remote = RemotePath.Combine(directory, relative);
            contents[remote] = content;
            requests.Add(new TransferRequest(TransferDirection.Upload, local, remote));
        }

        var options = new TransferOptions
        {
            MaxConcurrency = 3,
            RetryPolicy = new RetryPolicy(10, [TimeSpan.FromMilliseconds(100)], jitterRatio: 0),
            ConnectPolicy = new RetryPolicy(10, [TimeSpan.FromMilliseconds(200)], jitterRatio: 0),
        };

        // 中継をはさみ、転送の途中で何度も接続を切る
        await using var proxy = new FlakyTcpProxy(server.Host, server.Port);
        var uploads = new TransferScheduler(server.CreateFactory("127.0.0.1", proxy.Port), options);
        uploads.Enqueue(requests);
        using var chaos = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dropping = DropRepeatedlyAsync(proxy, TimeSpan.FromMilliseconds(250), times: 8, chaos.Token);

        var uploaded = await uploads.RunAsync(cancellationToken);
        await chaos.CancelAsync();
        await dropping;

        Assert.Equal(TransferRunOutcome.Finished, uploaded.Outcome);
        Assert.Equal(400, uploaded.Summary.Completed);
        Assert.True(proxy.DroppedConnections > 0, "テストの途中で接続を切れていません");

        // 届いた中身を、ふつうの接続で全部ダウンロードして確かめる
        string LocalCopy(string remote) => Path.Combine(temp.Path, "down", remote.TrimStart('/'));
        var downloads = new TransferScheduler(server.Factory, options);
        downloads.Enqueue(contents.Keys.Select(remote => new TransferRequest(TransferDirection.Download, LocalCopy(remote), remote)));
        var downloaded = await downloads.RunAsync(cancellationToken);

        Assert.Equal(400, downloaded.Summary.Completed);
        foreach (var (remote, content) in contents)
            Assert.Equal(content, File.ReadAllBytes(LocalCopy(remote)));
    }

    private static async Task DropRepeatedlyAsync(FlakyTcpProxy proxy, TimeSpan interval, int times, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < times; i++)
            {
                await Task.Delay(interval, cancellationToken);
                proxy.DropAll();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
