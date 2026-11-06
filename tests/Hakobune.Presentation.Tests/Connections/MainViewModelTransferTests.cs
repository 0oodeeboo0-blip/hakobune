using System.Text;
using Hakobune.Core.Connections;
using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Connections;

/// <summary>接続してから、アップロード・ダウンロードして、送信待ちが次へ引き継がれるまで。</summary>
public sealed class MainViewModelTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeLoginServer _server = new();
    private readonly ScriptedPrompts _prompts = new();
    private readonly ScriptedDialogs _dialogs = new();
    private readonly ScriptedTransferDialogs _transferDialogs = new();
    private readonly ScriptedSyncDialogs _syncDialogs = new();
    private readonly MemorySecretStore _secrets = new();
    private readonly ConnectionProfile _profile = new() { Name = "ショップ", Host = "example.com", UserName = "web" };

    public MainViewModelTransferTests()
    {
        Directory.CreateDirectory(LocalFolder);
        _server.Folders["/home/web"] =
        [
            new RemoteItem("notes.txt", "/home/web/notes.txt", RemoteItemType.File, 4, DateTimeOffset.UtcNow),
        ];
        _server.Files["/home/web/notes.txt"] = "memo"u8.ToArray();
        _secrets.Write(_profile.Id, "right");
    }

    private string DataDirectory => Path.Combine(_root, "data");

    private string LocalFolder => Path.Combine(_root, "site");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<MainViewModel> ConnectAsync()
    {
        var services = new AppServices(DataDirectory, _secrets, _prompts, _dialogs, _transferDialogs, SyncDialogs: _syncDialogs);
        var connector = new ConnectionService(TrustStore.InMemory(), _secrets, _prompts, _server.Builder);
        var main = new MainViewModel(
            new FilePaneViewModel(PaneKind.Local),
            new FilePaneViewModel(PaneKind.Remote),
            new TransferListViewModel(EmptyTransferSource.Instance, null, null),
            new ConnectionListViewModel([_profile]),
            new ConnectionStatusViewModel(),
            services,
            connector);
        await main.Local.SetSourceAsync(new LocalFileSource(showComputerRoot: false), LocalFolder, TestContext.Current.CancellationToken);
        await main.ConnectAsync(main.Connections.Items[0]);
        Assert.True(main.IsConnected);
        return main;
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

    private void WriteLocal(string relativePath, string content, DateTimeOffset? modified = null)
    {
        var path = Path.Combine(LocalFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modified is { } time)
            File.SetLastWriteTimeUtc(path, time.UtcDateTime);
    }

    [Fact]
    public async Task Sync_sends_only_what_changed_and_can_delete_what_is_only_on_the_server()
    {
        var earlier = DateTimeOffset.UtcNow.AddDays(-1);
        _server.Folders["/home/web"] =
        [
            new RemoteItem("notes.txt", "/home/web/notes.txt", RemoteItemType.File, 4, earlier),
            new RemoteItem("index.html", "/home/web/index.html", RemoteItemType.File, 5, earlier),
            new RemoteItem("old.html", "/home/web/old.html", RemoteItemType.File, 3, earlier),
        ];
        _server.Files["/home/web/index.html"] = "hello"u8.ToArray();
        _server.Files["/home/web/old.html"] = "old"u8.ToArray();
        WriteLocal("notes.txt", "memo", earlier); // 同じ
        WriteLocal("index.html", "hello again"); // 直した
        WriteLocal(Path.Combine("css", "site.css"), "body {}"); // 新しい
        WriteLocal("Thumbs.db", "thumbs"); // 送らない
        _syncDialogs.Choose = sync => sync.DeleteOnlyOnTarget = true;
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();
        Assert.True(main.CanSync);
        Assert.True(main.SyncUploadCommand.CanExecute(null));

        await main.SyncUploadCommand.ExecuteAsync(null);
        await WaitForAsync(() => main.PendingTransferCount == 0 && _server.Operations.Contains("rm /home/web/old.html"));

        var review = Assert.Single(_syncDialogs.Reviews);
        Assert.Equal(new[] { "css/site.css", "index.html" }, review.SendRows.Select(row => row.Path).ToArray());
        Assert.Equal(1, review.Result?.SameCount);
        Assert.Equal(1, review.Result?.ExcludedCount);
        Assert.Equal("hello again", Encoding.UTF8.GetString(_server.Files["/home/web/index.html"]));
        Assert.Equal("body {}", Encoding.UTF8.GetString(_server.Files["/home/web/css/site.css"]));
        Assert.False(_server.Files.ContainsKey("/home/web/Thumbs.db"));
        Assert.Equal(3, main.Transfers.TotalCount);
    }

    [Fact]
    public async Task Sync_can_receive_only_what_changed_and_never_deletes_on_this_computer()
    {
        var earlier = DateTimeOffset.UtcNow.AddHours(-2);
        _server.Folders["/home/web"] =
        [
            new RemoteItem("notes.txt", "/home/web/notes.txt", RemoteItemType.File, 4, earlier),
            new RemoteItem("news.txt", "/home/web/news.txt", RemoteItemType.File, 4, earlier),
        ];
        _server.Files["/home/web/news.txt"] = "news"u8.ToArray();
        WriteLocal("notes.txt", "memo", earlier);
        WriteLocal("mine.txt", "mine");
        // 受け取るときは、消すことを選んでも、パソコンのものは消さない
        _syncDialogs.Choose = sync => sync.DeleteOnlyOnTarget = true;
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();

        await main.SyncDownloadCommand.ExecuteAsync(null);
        await WaitForAsync(() => main.PendingTransferCount == 0 && File.Exists(Path.Combine(LocalFolder, "news.txt")));

        Assert.Equal("news", File.ReadAllText(Path.Combine(LocalFolder, "news.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(LocalFolder, "mine.txt")));
        Assert.Equal(1, main.Transfers.TotalCount);
    }

    [Fact]
    public async Task Stopping_the_sync_review_sends_nothing()
    {
        WriteLocal("index.html", "hello");
        _syncDialogs.Answer = false;
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();

        await main.SyncUploadCommand.ExecuteAsync(null);

        Assert.Single(_syncDialogs.Reviews);
        Assert.Equal(0, main.Transfers.TotalCount);
        Assert.False(_server.Files.ContainsKey("/home/web/index.html"));
    }

    [Fact]
    public async Task Selected_files_and_folders_are_uploaded_to_the_open_server_folder()
    {
        WriteLocal("index.html", "hello");
        WriteLocal(Path.Combine("css", "site.css"), "body {}");
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();
        Assert.False(main.UploadCommand.CanExecute(null));

        main.Local.UpdateSelection([.. main.Local.Items]);
        Assert.True(main.UploadCommand.CanExecute(null));
        await main.UploadCommand.ExecuteAsync(null);
        await WaitForAsync(() => _server.Files.Count == 3 && main.PendingTransferCount == 0);

        Assert.Equal("hello", Encoding.UTF8.GetString(_server.Files["/home/web/index.html"]));
        Assert.Equal("body {}", Encoding.UTF8.GetString(_server.Files["/home/web/css/site.css"]));
        Assert.Empty(_transferDialogs.ConflictQuestions);
        Assert.Equal(2, main.Transfers.TotalCount);
    }

    [Fact]
    public async Task Files_that_are_not_sent_stay_behind()
    {
        WriteLocal("index.html", "hello");
        WriteLocal("Thumbs.db", "thumbs");
        WriteLocal("~$見積書.xlsx", "lock");
        WriteLocal(Path.Combine("images", "Thumbs.db"), "thumbs");
        WriteLocal(Path.Combine("images", "logo.png"), "png");
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();
        Assert.True(main.Local.Items.Single(row => row.Name == "Thumbs.db").IsExcluded);
        Assert.False(main.Local.Items.Single(row => row.Name == "index.html").IsExcluded);

        main.Local.UpdateSelection([.. main.Local.Items]);
        await main.UploadCommand.ExecuteAsync(null);
        await WaitForAsync(() => main.PendingTransferCount == 0 && main.Transfers.CompletedCount == 2);

        Assert.True(_server.Files.ContainsKey("/home/web/index.html"));
        Assert.True(_server.Files.ContainsKey("/home/web/images/logo.png"));
        Assert.DoesNotContain(_server.Files.Keys, path => path.Contains("Thumbs.db", StringComparison.Ordinal) || path.Contains("~$", StringComparison.Ordinal));
        Assert.Contains(main.Log.GetSnapshot().Entries, entry => entry.Message.Contains("送らないファイル 3 件を除きました", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choosing_only_files_that_are_not_sent_explains_why()
    {
        WriteLocal("Thumbs.db", "thumbs");
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();

        main.Local.UpdateSelection([main.Local.Items.Single(row => row.Name == "Thumbs.db")]);
        await main.UploadCommand.ExecuteAsync(null);

        Assert.True(main.Local.HasMessage);
        Assert.Equal("送るものがありません", main.Local.MessageTitle);
        Assert.DoesNotContain(_server.Files.Keys, path => path.EndsWith("Thumbs.db", StringComparison.Ordinal));
        Assert.Equal(0, main.Transfers.TotalCount);
    }

    [Fact]
    public async Task Empty_folders_are_made_on_the_other_side_too()
    {
        Directory.CreateDirectory(Path.Combine(LocalFolder, "uploads"));
        WriteLocal(Path.Combine("css", "site.css"), "body {}");
        _server.Folders["/home/web"].Add(new RemoteItem("cache", "/home/web/cache", RemoteItemType.Directory, 0, DateTimeOffset.UtcNow));
        _server.Folders["/home/web/cache"] = [];
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();

        main.Local.UpdateSelection([.. main.Local.Items]);
        await main.UploadCommand.ExecuteAsync(null);
        await WaitForAsync(() => main.PendingTransferCount == 0 && main.Transfers.TotalCount == 2 && main.Transfers.CompletedCount == 2);
        Assert.Contains("mkdir /home/web/uploads", _server.Operations);
        Assert.Equal("body {}", Encoding.UTF8.GetString(_server.Files["/home/web/css/site.css"]));
        Assert.Contains(main.Transfers.Rows, row => row.IsFolder && row.Name == "uploads");

        main.Remote.UpdateSelection([main.Remote.Items.Single(row => row.Name == "cache")]);
        await main.DownloadCommand.ExecuteAsync(null);
        await WaitForAsync(() => Directory.Exists(Path.Combine(LocalFolder, "cache")));
    }

    [Fact]
    public async Task Selected_server_files_are_downloaded_to_the_open_local_folder()
    {
        using var main = await ConnectAsync();
        main.Remote.UpdateSelection([main.Remote.Items.Single(row => row.Name == "notes.txt")]);
        Assert.True(main.DownloadCommand.CanExecute(null));

        await main.DownloadCommand.ExecuteAsync(null);
        var local = Path.Combine(LocalFolder, "notes.txt");
        await WaitForAsync(() => File.Exists(local) && main.PendingTransferCount == 0);

        Assert.Equal("memo", await File.ReadAllTextAsync(local, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_name_that_already_exists_asks_what_to_do()
    {
        WriteLocal("notes.txt", "new memo");
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();
        _transferDialogs.ConflictAnswer = null; // やめる

        main.Local.UpdateSelection([.. main.Local.Items]);
        await main.UploadCommand.ExecuteAsync(null);

        var question = Assert.Single(_transferDialogs.ConflictQuestions);
        Assert.Equal(TransferDirection.Upload, question.Direction);
        Assert.Equal(new[] { "notes.txt" }, question.Names.ToArray());
        Assert.Equal(0, main.Transfers.TotalCount);
        Assert.Equal("memo", Encoding.UTF8.GetString(_server.Files["/home/web/notes.txt"]));
    }

    [Fact]
    public async Task Disconnecting_during_transfers_asks_first_and_the_rest_waits_for_next_time()
    {
        for (var i = 0; i < 3; i++)
            WriteLocal($"photo{i}.jpg", new string('x', 100 + i));
        var never = new TaskCompletionSource();
        _server.BeforeUpload = token => never.Task.WaitAsync(token);
        using var main = await ConnectAsync();
        await main.Local.RefreshAsync();
        main.Local.UpdateSelection([.. main.Local.Items]);
        await main.UploadCommand.ExecuteAsync(null);
        await WaitForAsync(() => main.Transfers.Rows.Any(row => row.State == TransferState.Running));

        // いったん断る
        _transferDialogs.StopAnswer = false;
        await main.DisconnectCommand.ExecuteAsync(null);
        Assert.True(main.IsConnected);
        Assert.Equal((3, false), Assert.Single(_transferDialogs.StopQuestions));

        // 止めて切断する。残りは保存される
        _transferDialogs.StopAnswer = true;
        await main.DisconnectCommand.ExecuteAsync(null);
        Assert.False(main.IsConnected);
        Assert.Equal(0, main.Transfers.TotalCount);
        Assert.True(File.Exists(Path.Combine(DataDirectory, "queues", $"{_profile.Id:N}.json")));

        // 次に接続すると、残りが止めた状態で戻る。再開すると送る
        _server.BeforeUpload = null;
        await main.ConnectAsync(main.Connections.Items[0]);
        Assert.Equal(3, main.Transfers.TotalCount);
        Assert.True(main.Transfers.IsPaused);
        Assert.StartsWith("前回の残りが 3 件あります", main.Transfers.PauseText, StringComparison.Ordinal);

        main.Transfers.PauseAllCommand.Execute(null);
        await WaitForAsync(() => _server.Files.Count == 4 && main.PendingTransferCount == 0);
    }
}
