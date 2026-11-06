using Hakobune.Core.Backups;
using Hakobune.Core.Connections;
using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Connections;

/// <summary>新しいフォルダ、名前の変更、削除、属性の変更、ドラッグ＆ドロップ。</summary>
public sealed class MainViewModelFileOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeLoginServer _server = new();
    private readonly ScriptedPrompts _prompts = new();
    private readonly ScriptedFileDialogs _fileDialogs = new();
    private readonly MemorySecretStore _secrets = new();
    private readonly ConnectionProfile _profile = new() { Name = "ショップ", Host = "example.com", UserName = "web" };

    public MainViewModelFileOperationTests()
    {
        Directory.CreateDirectory(LocalFolder);
        _server.Folders["/home/web"] =
        [
            new RemoteItem("notes.txt", "/home/web/notes.txt", RemoteItemType.File, 4, DateTimeOffset.UtcNow, "-rw-r--r--"),
            new RemoteItem("old", "/home/web/old", RemoteItemType.Directory, 0, DateTimeOffset.UtcNow, "drwxr-xr-x"),
        ];
        _server.Folders["/home/web/old"] = [];
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

    private async Task<MainViewModel> CreateAsync(bool connect = true)
    {
        var services = new AppServices(Path.Combine(_root, "data"), _secrets, _prompts, new ScriptedDialogs(), new ScriptedTransferDialogs(), _fileDialogs);
        var main = new MainViewModel(
            new FilePaneViewModel(PaneKind.Local),
            new FilePaneViewModel(PaneKind.Remote),
            new TransferListViewModel(EmptyTransferSource.Instance, null, null),
            new ConnectionListViewModel([_profile]),
            new ConnectionStatusViewModel(),
            services,
            new ConnectionService(TrustStore.InMemory(), _secrets, _prompts, _server.Builder));
        await main.Local.SetSourceAsync(new LocalFileSource(showComputerRoot: false), LocalFolder, TestContext.Current.CancellationToken);
        if (connect)
            await main.ConnectAsync(main.Connections.Items[0]);
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

    [Fact]
    public async Task A_new_local_folder_is_created_and_selected()
    {
        Directory.CreateDirectory(Path.Combine(LocalFolder, "新しいフォルダ"));
        using var main = await CreateAsync(connect: false);
        await main.Local.RefreshAsync();
        main.Local.Activate();
        _fileDialogs.Names.Enqueue("写真");

        await main.NewFolderCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(Path.Combine(LocalFolder, "写真")));
        // すでにある名前は避けて、最初の案を出す
        Assert.Equal("新しいフォルダ (2)", _fileDialogs.NameQuestions[0].Initial);
        Assert.Equal("写真", main.Local.FocusItem?.Name);
    }

    [Fact]
    public async Task Names_that_cannot_be_used_are_refused()
    {
        File.WriteAllText(Path.Combine(LocalFolder, "a.txt"), "a");
        using var main = await CreateAsync(connect: false);
        await main.Local.RefreshAsync();
        main.Local.Activate();
        _fileDialogs.Names.Enqueue("a.txt");

        await main.NewFolderCommand.ExecuteAsync(null);

        Assert.Equal("同じ名前のものがあります", _fileDialogs.NameQuestions[0].ProblemForAnswer);
        Assert.False(Directory.Exists(Path.Combine(LocalFolder, "a.txt")));
    }

    [Fact]
    public async Task Local_files_are_renamed_and_deleted()
    {
        File.WriteAllText(Path.Combine(LocalFolder, "draft.txt"), "x");
        using var main = await CreateAsync(connect: false);
        await main.Local.RefreshAsync();
        main.Local.Activate();
        main.Local.UpdateSelection([.. main.Local.Items]);
        _fileDialogs.Names.Enqueue("final.txt");

        await main.RenameCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(LocalFolder, "final.txt")));
        Assert.False(File.Exists(Path.Combine(LocalFolder, "draft.txt")));

        main.Local.UpdateSelection([.. main.Local.Items]);
        await main.DeleteCommand.ExecuteAsync(null);

        var question = Assert.Single(_fileDialogs.DeleteQuestions);
        Assert.False(question.Remote);
        Assert.Equal(new[] { "final.txt" }, question.Names.ToArray());
        Assert.False(File.Exists(Path.Combine(LocalFolder, "final.txt")));
        Assert.Empty(main.Local.Items);
    }

    [Fact]
    public async Task Server_folders_are_created_renamed_and_deleted_through_the_browsing_connection()
    {
        using var main = await CreateAsync();
        main.Remote.Activate();
        _fileDialogs.Names.Enqueue("images");
        await main.NewFolderCommand.ExecuteAsync(null);

        main.Remote.UpdateSelection([main.Remote.Items.Single(row => row.Name == "old")]);
        _fileDialogs.Names.Enqueue("archive");
        await main.RenameCommand.ExecuteAsync(null);

        main.Remote.UpdateSelection([.. main.Remote.Items]);
        await main.DeleteCommand.ExecuteAsync(null);

        Assert.Contains("mkdir /home/web/images", _server.Operations);
        Assert.Contains("mv /home/web/old /home/web/archive", _server.Operations);
        Assert.Contains("rm /home/web/notes.txt", _server.Operations);
        Assert.Contains("rmdir /home/web/old", _server.Operations);
        var question = Assert.Single(_fileDialogs.DeleteQuestions);
        Assert.True(question.Remote);
        // 消す前に控えを取るので、元に戻せる
        Assert.True(question.Recoverable);
        var backups = main.Backups!;
        var backup = Assert.Single(backups.GetEntries(_profile.Id));
        Assert.Equal("/home/web/notes.txt", backup.OriginalPath);
        Assert.Equal(BackupKind.ServerDeleted, backup.Kind);
        Assert.Equal("memo", await File.ReadAllTextAsync(backups.GetFullPath(backup), TestContext.Current.CancellationToken));
        Assert.Equal("削除", backups.GetBatch(backup.Batch)?.Label);
    }

    [Fact]
    public async Task A_server_file_whose_backup_cannot_be_taken_is_not_deleted()
    {
        // 一覧にはあるが、受け取れない（控えを取れない）ファイル
        _server.Folders["/home/web"] =
        [
            new RemoteItem("ghost.txt", "/home/web/ghost.txt", RemoteItemType.File, 3, DateTimeOffset.UtcNow, "-rw-r--r--"),
        ];
        using var main = await CreateAsync();
        main.Remote.Activate();
        main.Remote.UpdateSelection([.. main.Remote.Items]);

        await main.DeleteCommand.ExecuteAsync(null);

        Assert.DoesNotContain("rm /home/web/ghost.txt", _server.Operations);
        Assert.Empty(main.Backups!.GetEntries(_profile.Id));
        Assert.Equal("削除できませんでした", main.Remote.MessageTitle);
        Assert.StartsWith("控えを取れなかったので", main.Remote.MessageReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_permissions_start_from_the_current_ones()
    {
        using var main = await CreateAsync();
        main.Remote.UpdateSelection([main.Remote.Items.Single(row => row.Name == "notes.txt")]);
        _fileDialogs.Permissions = Convert.ToInt32("600", 8);
        Assert.True(main.ChangePermissionsCommand.CanExecute(null));

        await main.ChangePermissionsCommand.ExecuteAsync(null);

        Assert.Equal(Convert.ToInt32("644", 8), Assert.Single(_fileDialogs.PermissionQuestions));
        Assert.Contains("chmod 600 /home/web/notes.txt", _server.Operations);
    }

    [Fact]
    public async Task Dropping_local_rows_on_the_server_side_uploads_them()
    {
        File.WriteAllText(Path.Combine(LocalFolder, "drop.txt"), "dropped");
        using var main = await CreateAsync();
        await main.Local.RefreshAsync();

        main.Remote.DropRows(main.Local, [.. main.Local.Items]);
        await WaitForAsync(() => _server.Files.ContainsKey("/home/web/drop.txt") && main.PendingTransferCount == 0);

        Assert.Equal("dropped", System.Text.Encoding.UTF8.GetString(_server.Files["/home/web/drop.txt"]));
    }

    [Fact]
    public async Task Dropping_server_rows_on_the_local_side_downloads_them()
    {
        using var main = await CreateAsync();

        main.Local.DropRows(main.Remote, [main.Remote.Items.Single(row => row.Name == "notes.txt")]);
        var local = Path.Combine(LocalFolder, "notes.txt");
        await WaitForAsync(() => File.Exists(local) && main.PendingTransferCount == 0);

        Assert.Equal("memo", await File.ReadAllTextAsync(local, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ok.txt", PaneKind.Local, null)]
    [InlineData("a:b", PaneKind.Local, "\\ : * ? \" < > | は名前に使えません")]
    [InlineData("a:b", PaneKind.Remote, null)]
    [InlineData("con.txt", PaneKind.Local, "「con」は Windows が使う名前なので使えません")]
    [InlineData("末尾.", PaneKind.Local, "名前の終わりに「.」や空白は使えません")]
    [InlineData("a/b", PaneKind.Remote, "「/」は名前に使えません")]
    [InlineData("  ", PaneKind.Remote, "名前を入れてください")]
    [InlineData("..", PaneKind.Remote, "「.」と「..」は名前に使えません")]
    public void File_names_are_checked_for_each_side(string name, PaneKind kind, string? problem) =>
        Assert.Equal(problem, FileNames.Validate(name, kind));
}
