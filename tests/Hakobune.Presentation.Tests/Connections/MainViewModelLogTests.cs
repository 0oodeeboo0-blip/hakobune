using Hakobune.Core.Connections;
using Hakobune.Core.Logging;
using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Connections;

/// <summary>接続・ファイルの操作・転送が、下の「ログ」に残ること。</summary>
public sealed class MainViewModelLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeLoginServer _server = new();
    private readonly ScriptedPrompts _prompts = new();
    private readonly ScriptedFileDialogs _fileDialogs = new();
    private readonly MemorySecretStore _secrets = new();
    private readonly ActivityLog _log = new();
    private readonly ConnectionProfile _profile = new() { Name = "ショップ", Host = "example.com", UserName = "web" };

    public MainViewModelLogTests()
    {
        Directory.CreateDirectory(LocalFolder);
        _server.Folders["/home/web"] =
        [
            new RemoteItem("notes.txt", "/home/web/notes.txt", RemoteItemType.File, 4, DateTimeOffset.UtcNow, "-rw-r--r--"),
        ];
        _server.Files["/home/web/notes.txt"] = "memo"u8.ToArray();
        _secrets.Write(_profile.Id, "right");
    }

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

    private async Task<MainViewModel> CreateAsync()
    {
        var services = new AppServices(Path.Combine(_root, "data"), _secrets, _prompts, new ScriptedDialogs(), new ScriptedTransferDialogs(), _fileDialogs);
        var main = new MainViewModel(
            new FilePaneViewModel(PaneKind.Local),
            new FilePaneViewModel(PaneKind.Remote),
            new TransferListViewModel(EmptyTransferSource.Instance, null, null),
            new ConnectionListViewModel([_profile]),
            new ConnectionStatusViewModel(),
            services,
            new ConnectionService(TrustStore.InMemory(), _secrets, _prompts, _server.Builder, _log),
            log: _log);
        await main.Local.SetSourceAsync(new LocalFileSource(showComputerRoot: false), LocalFolder, TestContext.Current.CancellationToken);
        return main;
    }

    private IReadOnlyList<LogEntry> Entries => _log.GetSnapshot().Entries;

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
    public async Task Connecting_and_disconnecting_are_written_with_the_server_name()
    {
        using var main = await CreateAsync();

        await main.ConnectAsync(main.Connections.Items[0]);
        await main.DisconnectAsync();

        var messages = Entries.Where(entry => entry.Kind != LogKind.Detail).Select(entry => entry.Message).ToArray();
        Assert.Equal(new[] { "ショップ に接続しています（SFTP・example.com:22）", "接続しました（SFTP・暗号化）", "切断しました" }, messages);
        Assert.All(Entries, entry => Assert.Equal("ショップ", entry.Source));
        // サーバーとのやり取りは「通信の詳細」に入る
        Assert.Contains(Entries, entry => entry is { Kind: LogKind.Detail, Message: "Response: 230 web がログインしました" });
    }

    [Fact]
    public async Task A_failed_connection_is_written_with_the_reason()
    {
        _server.ConnectFault = TransferFailure.Transient(FailureReason.Timeout);
        using var main = await CreateAsync();

        await main.ConnectAsync(main.Connections.Items[0]);

        var entry = Entries[^1];
        Assert.Equal(LogKind.Error, entry.Kind);
        Assert.Equal("接続できませんでした: サーバーの応答がありません", entry.Message);
    }

    [Fact]
    public async Task Server_operations_are_written()
    {
        using var main = await CreateAsync();
        await main.ConnectAsync(main.Connections.Items[0]);
        main.Remote.Activate();
        _fileDialogs.Names.Enqueue("images");
        await main.NewFolderCommand.ExecuteAsync(null);
        main.Remote.UpdateSelection([main.Remote.Items.Single(row => row.Name == "notes.txt")]);
        _fileDialogs.Permissions = Convert.ToInt32("600", 8);
        await main.ChangePermissionsCommand.ExecuteAsync(null);

        var messages = Entries.Select(entry => entry.Message).ToList();
        Assert.Contains("フォルダを作りました: /home/web/images", messages);
        Assert.Contains("属性を 600 にしました: /home/web/notes.txt", messages);
    }

    [Fact]
    public async Task Transfers_are_written_one_by_one()
    {
        File.WriteAllText(Path.Combine(LocalFolder, "index.html"), "hello");
        using var main = await CreateAsync();
        await main.ConnectAsync(main.Connections.Items[0]);
        await main.Local.RefreshAsync();
        main.Local.UpdateSelection([.. main.Local.Items]);

        await main.UploadCommand.ExecuteAsync(null);
        await WaitForAsync(() => Entries.Any(entry => entry.Message.StartsWith("転送が終わりました", StringComparison.Ordinal)));

        var messages = Entries.Select(entry => entry.Message).ToList();
        Assert.Contains("アップロードに 1 件を加えました（5 バイト）", messages);
        Assert.Contains("送りました: /home/web/index.html（5 バイト）", messages);
        Assert.Contains("転送が終わりました: 完了 1 件（5 バイト）", messages);
    }

    [Fact]
    public void The_log_tab_opens_the_transfer_list()
    {
        var transfers = new TransferListViewModel(EmptyTransferSource.Instance, null, null) { IsExpanded = false };
        using var main = new MainViewModel(
            new FilePaneViewModel(PaneKind.Local),
            new FilePaneViewModel(PaneKind.Remote),
            transfers,
            new ConnectionListViewModel([]),
            new ConnectionStatusViewModel());

        main.ShowLogCommand.Execute(null);

        Assert.True(transfers.IsExpanded);
        Assert.True(transfers.IsLogSelected);
        Assert.DoesNotContain(transfers.Tabs, tab => tab.IsSelected);

        // 転送のタブに戻ると、前に選んでいたタブになる
        transfers.SelectTab(TransferTab.All);
        Assert.False(transfers.IsLogSelected);
        Assert.True(transfers.Tabs.Single(tab => tab.Tab == TransferTab.All).IsSelected);
    }
}
