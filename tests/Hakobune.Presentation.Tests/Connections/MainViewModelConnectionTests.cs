using Hakobune.Core.Connections;
using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Connections;

public sealed class MainViewModelConnectionTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeLoginServer _server = new();
    private readonly ScriptedPrompts _prompts = new();
    private readonly ScriptedDialogs _dialogs = new();
    private readonly MemorySecretStore _secrets = new();

    public MainViewModelConnectionTests()
    {
        _server.Folders["/home/web"] =
        [
            new RemoteItem("public_html", "/home/web/public_html", RemoteItemType.Directory, 0, DateTimeOffset.UtcNow),
            new RemoteItem("notes.txt", "/home/web/notes.txt", RemoteItemType.File, 10, DateTimeOffset.UtcNow),
        ];
        _server.Folders["/home/web/public_html"] = [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private MainViewModel Create(params ConnectionProfile[] profiles) => Create(_secrets, profiles);

    private MainViewModel Create(ISecretStore secrets, params ConnectionProfile[] profiles)
    {
        var services = new AppServices(_dataDirectory, secrets, _prompts, _dialogs);
        var connector = new ConnectionService(TrustStore.InMemory(), secrets, _prompts, _server.Builder);
        return new MainViewModel(
            new FilePaneViewModel(PaneKind.Local),
            new FilePaneViewModel(PaneKind.Remote),
            new TransferListViewModel(EmptyTransferSource.Instance, null, null),
            new ConnectionListViewModel(profiles),
            new ConnectionStatusViewModel(),
            services,
            connector);
    }

    [Fact]
    public async Task Connecting_shows_the_server_files_and_the_secure_connection()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        _secrets.Write(profile.Id, "right");
        using var main = Create(profile);
        var item = main.Connections.Items[0];

        await main.ConnectAsync(item);

        Assert.True(main.IsConnected);
        Assert.True(item.IsConnected);
        Assert.Same(item, main.Connections.Selected);
        Assert.Equal("/home/web", main.Remote.CurrentPath);
        Assert.Equal(new[] { "public_html", "notes.txt" }, main.Remote.Items.Select(row => row.Name).ToArray());
        Assert.Equal("SFTP・暗号化", main.Connection.SecurityText);
        Assert.Equal("example.com", main.Connection.Host);
        Assert.NotNull(main.Session?.Factory);
    }

    [Fact]
    public async Task The_remote_folder_of_the_connection_is_opened_first()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web", RemoteDirectory = "/home/web/public_html" };
        using var main = Create(profile);

        await main.ConnectAsync(main.Connections.Items[0], password: "right");

        Assert.Equal("/home/web/public_html", main.Remote.CurrentPath);
    }

    [Fact]
    public async Task A_failed_connection_explains_why_and_can_be_retried()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        _secrets.Write(profile.Id, "right");
        _server.ConnectFault = TransferFailure.Transient(FailureReason.Timeout);
        using var main = Create(profile);

        await main.ConnectAsync(main.Connections.Items[0]);

        Assert.False(main.IsConnected);
        Assert.True(main.Remote.HasMessage);
        Assert.Equal("接続できませんでした", main.Remote.MessageTitle);
        Assert.Equal("サーバーの応答がありません", main.Remote.MessageReason);
        Assert.True(main.Remote.RetryCommand.CanExecute(null));
        Assert.Equal("接続していません", main.Connection.Name);

        _server.ConnectFault = null;
        await main.Remote.RetryCommand.ExecuteAsync(null);

        Assert.True(main.IsConnected);
        Assert.False(main.Remote.HasMessage);
    }

    [Fact]
    public async Task Disconnecting_empties_the_server_side()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        using var main = Create(profile);
        await main.ConnectAsync(main.Connections.Items[0], password: "right");

        await main.DisconnectCommand.ExecuteAsync(null);

        Assert.False(main.IsConnected);
        Assert.False(main.Remote.HasSource);
        Assert.Empty(main.Remote.Items);
        Assert.Equal("接続していません", main.Remote.EmptyText);
        Assert.False(main.Connections.Items[0].IsConnected);
    }

    [Fact]
    public async Task A_new_connection_is_saved_and_connected()
    {
        using var main = Create();
        var profile = new ConnectionProfile { Name = "新しい", Host = "example.com", UserName = "web" };
        _dialogs.Edits.Enqueue(new ConnectionEditResult(profile, "right", SavePassword: true));

        await main.NewConnectionCommand.ExecuteAsync(null);

        Assert.True(main.IsConnected);
        Assert.Equal("right", _secrets.Read(profile.Id));
        var saved = await ConnectionStore.LoadAsync(Path.Combine(_dataDirectory, "connections.json"), TestContext.Current.CancellationToken);
        Assert.Equal(profile, Assert.Single(saved));
    }

    [Fact]
    public async Task Deleting_a_connection_forgets_its_password_and_disconnects()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        _secrets.Write(profile.Id, "right");
        using var main = Create(profile);
        var item = main.Connections.Items[0];
        await main.ConnectAsync(item);

        await main.DeleteConnectionCommand.ExecuteAsync(item);

        Assert.False(main.IsConnected);
        Assert.Null(_secrets.Read(profile.Id));
        Assert.True(main.Connections.IsEmpty);
        Assert.Empty(await ConnectionStore.LoadAsync(Path.Combine(_dataDirectory, "connections.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Duplicating_makes_an_independent_copy()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        using var main = Create(profile);

        await main.DuplicateConnectionCommand.ExecuteAsync(main.Connections.Items[0]);

        Assert.Equal(2, main.Connections.Items.Count);
        var copy = main.Connections.Items[1];
        Assert.NotEqual(profile.Id, copy.Profile.Id);
        Assert.Equal("ショップ のコピー", copy.Name);
        Assert.Same(copy, main.Connections.Selected);
    }

    [Fact]
    public async Task Choosing_another_connection_while_connecting_gives_up_the_first()
    {
        var slow = new ConnectionProfile { Name = "遅い", Host = "slow.example.com", UserName = "web" };
        var fast = new ConnectionProfile { Name = "速い", Host = "fast.example.com", UserName = "web" };
        _secrets.Write(slow.Id, "right");
        _secrets.Write(fast.Id, "right");
        var never = new TaskCompletionSource();
        _server.Delay = (profile, token) => profile.Host == slow.Host ? never.Task.WaitAsync(token) : Task.CompletedTask;
        using var main = Create(slow, fast);
        var (slowItem, fastItem) = (main.Connections.Items[0], main.Connections.Items[1]);

        var first = main.ConnectAsync(slowItem);
        Assert.True(main.IsConnecting);
        Assert.True(slowItem.IsConnecting);
        Assert.True(main.DisconnectCommand.CanExecute(null));
        Assert.Equal("遅い に接続しています…", main.Connection.Name);

        await main.ConnectAsync(fastItem);
        await first;

        Assert.True(main.IsConnected);
        Assert.False(main.IsConnecting);
        Assert.False(slowItem.IsConnecting);
        Assert.False(slowItem.IsConnected);
        Assert.True(fastItem.IsConnected);
        Assert.Same(fastItem, main.Connections.Selected);
        Assert.Equal("速い", main.Connection.Name);
        Assert.False(main.Remote.HasMessage);
        Assert.Equal("/home/web", main.Remote.CurrentPath);
    }

    [Fact]
    public async Task Disconnecting_while_connecting_gives_up_quietly()
    {
        var profile = new ConnectionProfile { Name = "遅い", Host = "slow.example.com", UserName = "web" };
        _secrets.Write(profile.Id, "right");
        var never = new TaskCompletionSource();
        _server.Delay = (_, token) => never.Task.WaitAsync(token);
        using var main = Create(profile);

        var connecting = main.ConnectAsync(main.Connections.Items[0]);
        await main.DisconnectCommand.ExecuteAsync(null);
        await connecting;

        Assert.False(main.IsConnected);
        Assert.False(main.IsConnecting);
        Assert.False(main.Remote.HasMessage);
        Assert.False(main.Remote.ShowsProgress);
        Assert.Equal("接続していません", main.Connection.Name);
        Assert.False(main.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_quick_connection_uses_the_url_and_saves_nothing()
    {
        using var main = Create();

        await main.QuickConnectAsync("sftp://web:right@example.com/home/web/public_html");

        Assert.True(main.IsConnected);
        var item = Assert.Single(main.Connections.Items);
        Assert.True(item.IsTemporary);
        Assert.Equal(ConnectionListViewModel.TemporaryGroupName, Assert.Single(main.Connections.Groups).Name);
        Assert.Equal("/home/web/public_html", main.Remote.CurrentPath);
        Assert.Null(_secrets.Read(item.Profile.Id));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "connections.json")));

        // 切ってからつなぎ直しても、URL のパスワードを使う（聞かない）
        await main.DisconnectAsync();
        await main.ConnectAsync(item);

        Assert.True(main.IsConnected);
        Assert.Equal(0, _prompts.PasswordQuestions);
    }

    [Fact]
    public async Task A_url_that_cannot_be_read_is_explained()
    {
        using var main = Create();

        await main.QuickConnectAsync("http://example.com/");

        Assert.False(main.IsConnected);
        Assert.Equal("URL を読み取れませんでした", main.Remote.MessageReason);
        Assert.True(main.Connections.IsEmpty);
    }

    [Fact]
    public async Task Saving_a_quick_connection_keeps_its_password_only_when_asked()
    {
        using var main = Create();
        await main.QuickConnectAsync("sftp://web:right@example.com/");
        var item = main.Connections.Items[0];
        _dialogs.Edits.Enqueue(new ConnectionEditResult(item.Profile with { Name = "ショップ" }, Password: null, SavePassword: true));

        await main.EditConnectionCommand.ExecuteAsync(item);

        Assert.True(Assert.Single(_dialogs.HasSavedPasswordShown));
        Assert.False(item.IsTemporary);
        Assert.Equal("right", _secrets.Read(item.Profile.Id));
        Assert.True(main.IsConnected); // 名前を付けただけなので、つなぎ直さない
        Assert.Equal("ショップ", main.Connection.Name);
        var saved = await ConnectionStore.LoadAsync(Path.Combine(_dataDirectory, "connections.json"), TestContext.Current.CancellationToken);
        Assert.Equal("ショップ", Assert.Single(saved).Name);
    }

    [Fact]
    public async Task Removing_a_quick_connection_does_not_ask()
    {
        using var main = Create();
        await main.QuickConnectAsync("sftp://web:right@example.com/");

        await main.DeleteConnectionCommand.ExecuteAsync(main.Connections.Items[0]);

        Assert.Equal(0, _dialogs.DeleteQuestions);
        Assert.False(main.IsConnected);
        Assert.True(main.Connections.IsEmpty);
    }

    [Fact]
    public async Task Turning_off_saving_forgets_the_saved_password()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        _secrets.Write(profile.Id, "right");
        using var main = Create(profile);
        _dialogs.Edits.Enqueue(new ConnectionEditResult(profile, Password: null, SavePassword: false));

        await main.EditConnectionCommand.ExecuteAsync(main.Connections.Items[0]);

        Assert.Null(_secrets.Read(profile.Id));
    }

    [Fact]
    public async Task Changing_the_server_reconnects()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        _secrets.Write(profile.Id, "right");
        using var main = Create(profile);
        var item = main.Connections.Items[0];
        await main.ConnectAsync(item);
        var before = _server.Logins;
        _dialogs.Edits.Enqueue(new ConnectionEditResult(profile with { Host = "new.example.com" }, Password: null, SavePassword: true));

        await main.EditConnectionCommand.ExecuteAsync(item);

        Assert.Equal(before + 1, _server.Logins);
        Assert.True(main.IsConnected);
        Assert.Equal("new.example.com", main.Connection.Host);
    }

    [Fact]
    public async Task A_broken_password_store_does_not_stop_connecting()
    {
        var profile = new ConnectionProfile { Name = "ショップ", Host = "example.com", UserName = "web" };
        _prompts.Passwords.Enqueue(new PasswordAnswer("right", Save: true));
        using var main = Create(new BrokenSecretStore(), profile);

        await main.ConnectAsync(main.Connections.Items[0]);

        Assert.True(main.IsConnected);
        Assert.Equal(1, _prompts.PasswordQuestions);
    }
}
