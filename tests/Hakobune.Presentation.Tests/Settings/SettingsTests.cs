using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Transfers;
using Hakobune.Presentation.Tests.Connections;

namespace Hakobune.Presentation.Tests.Settings;

/// <summary>設定の保存と、次に起動したときに前回の画面に戻ること、設定の画面で変えたものがすぐ効くこと。</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_root);

    private string DataDirectory => Path.Combine(_root, "data");

    private string SettingsFile => Path.Combine(DataDirectory, "settings.json");

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

    private AppServices Services(ISettingsDialogs? settingsDialogs = null) =>
        new(DataDirectory, new MemorySecretStore(), new ScriptedPrompts(), new ScriptedDialogs(), SettingsDialogs: settingsDialogs);

    private string MakeFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Settings_survive_a_restart()
    {
        var saved = new AppSettings
        {
            Theme = ThemeChoice.Dark,
            ShowHiddenFiles = true,
            UseCompactRows = true,
            Window = new WindowPlacement(10, 20, 1200, 800, IsMaximized: true),
            LastLocalFolder = @"C:\Users\user\Sites\shop",
            IsSidebarOpen = false,
            IsTransferListExpanded = false,
            TransferListHeight = 320,
            Backups = new BackupSettings { IsEnabled = false, KeepDays = 90, MaxTotalMegabytes = 5120 },
            Notifications = new NotificationSettings { NotifyWhenTransfersEnd = false, ShowTaskbarProgress = false },
            Columns = new FileColumnWidths { Size = 90, Modified = 140, Permissions = 70 },
        };

        Assert.True(AppSettings.TrySave(SettingsFile, saved));

        Assert.Equal(saved, await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken));
        // 人が読める形で、選んだ色は名前で書く
        Assert.Contains("\"Theme\": \"Dark\"", await File.ReadAllTextAsync(SettingsFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_or_broken_file_gives_the_defaults()
    {
        Assert.Equal(new AppSettings(), await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken));

        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllTextAsync(SettingsFile, "{ これは JSON ではない", TestContext.Current.CancellationToken);

        Assert.Equal(new AppSettings(), await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_last_screen_comes_back()
    {
        var folder = MakeFolder("site");
        var settings = new AppSettings
        {
            LastLocalFolder = folder,
            IsSidebarOpen = false,
            IsTransferListExpanded = false,
            UseCompactRows = true,
            TransferListHeight = 360,
        };

        using var main = await MainViewModel.CreateAsync(Services(), settings: settings);

        Assert.Equal(folder, main.Local.CurrentPath);
        Assert.False(main.IsSidebarOpen);
        Assert.False(main.Transfers.IsExpanded);
        Assert.Equal(360, main.Transfers.PanelHeight);
        Assert.True(main.Local.UseCompactRows);
        Assert.True(main.Remote.UseCompactRows);
    }

    [Fact]
    public async Task A_folder_that_is_gone_starts_from_home()
    {
        var settings = new AppSettings { LastLocalFolder = Path.Combine(_root, "消えたフォルダ") };

        using var main = await MainViewModel.CreateAsync(Services(), settings: settings);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), main.Local.CurrentPath);
    }

    [Fact]
    public async Task Saving_remembers_the_screen_and_the_window()
    {
        var folder = MakeFolder("site");
        using var main = await MainViewModel.CreateAsync(Services(), settings: new AppSettings { LastLocalFolder = folder });
        main.ToggleSidebarCommand.Execute(null);
        main.Transfers.IsExpanded = false;
        // 低すぎる高さにはしない
        main.Transfers.PanelHeight = 40;

        main.SaveSettings(new WindowPlacement(100, 50, 1300, 860, IsMaximized: false));

        var saved = await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken);
        Assert.Equal(folder, saved.LastLocalFolder);
        Assert.False(saved.IsSidebarOpen);
        Assert.False(saved.IsTransferListExpanded);
        Assert.Equal(TransferListViewModel.MinPanelHeight, saved.TransferListHeight);
        Assert.Equal(new WindowPlacement(100, 50, 1300, 860, false), saved.Window);

        // ウインドウの位置を渡さなければ、前回のままにする
        main.SaveSettings(window: null);
        Assert.Equal(saved.Window, (await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken)).Window);
    }

    [Fact]
    public async Task Hidden_files_appear_when_the_setting_is_turned_on()
    {
        var folder = MakeFolder("site");
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), "a", TestContext.Current.CancellationToken);
        var hidden = Path.Combine(folder, ".hidden-note.txt");
        await File.WriteAllTextAsync(hidden, "b", TestContext.Current.CancellationToken);
        // Linux では「.」で始まる名前が隠しファイル。Windows では属性で決まる
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var dialogs = new ScriptedSettingsDialogs(settings => settings.ShowHiddenFiles = true);
        using var main = await MainViewModel.CreateAsync(Services(dialogs), settings: new AppSettings { LastLocalFolder = folder });
        Assert.Equal(new[] { "index.html" }, main.Local.Items.Select(row => row.Name).ToArray());

        await main.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.Shown);
        Assert.Contains(main.Local.Items, row => row.Name == ".hidden-note.txt");
        Assert.True(main.Settings.ShowHiddenFiles);
        Assert.True((await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken)).ShowHiddenFiles);
    }

    [Fact]
    public async Task The_theme_and_row_height_change_at_once_and_are_saved()
    {
        var dialogs = new ScriptedSettingsDialogs(settings =>
        {
            Assert.Equal(0, settings.ThemeIndex);
            Assert.Equal("Windows の設定に合わせる", settings.ThemeNames[settings.ThemeIndex]);
            settings.ThemeIndex = 2;
            settings.UseCompactRows = true;
            // 選び直しの途中の -1 などは無視する
            settings.ThemeIndex = -1;
        });
        using var main = await MainViewModel.CreateAsync(Services(dialogs), settings: new AppSettings { LastLocalFolder = MakeFolder("site") });
        var changes = new List<string?>();
        main.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await main.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(ThemeChoice.Dark, main.Settings.Theme);
        Assert.Contains(nameof(MainViewModel.Settings), changes);
        Assert.True(main.Local.UseCompactRows);
        Assert.True(main.Remote.UseCompactRows);
        var saved = await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken);
        Assert.Equal(ThemeChoice.Dark, saved.Theme);
        Assert.True(saved.UseCompactRows);
    }

    [Fact]
    public async Task The_settings_screen_shows_the_saved_choices()
    {
        SettingsViewModel? shown = null;
        var dialogs = new ScriptedSettingsDialogs(settings => shown = settings);
        var saved = new AppSettings { Theme = ThemeChoice.Light, ShowHiddenFiles = true, UseCompactRows = true, LastLocalFolder = MakeFolder("site") };
        using var main = await MainViewModel.CreateAsync(Services(dialogs), settings: saved);

        await main.OpenSettingsCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(1, shown.ThemeIndex);
        Assert.True(shown.ShowHiddenFiles);
        Assert.True(shown.UseCompactRows);
        Assert.Equal(DataDirectory, shown.DataDirectory);
        Assert.StartsWith("バージョン ", shown.VersionText, StringComparison.Ordinal);
        // 開いただけでは何も書かない
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Files_not_to_send_can_be_changed_and_are_tried_on_the_open_folder()
    {
        var site = MakeFolder("site");
        File.WriteAllText(Path.Combine(site, "Thumbs.db"), "x");
        File.WriteAllText(Path.Combine(site, "index.html"), "x");
        Directory.CreateDirectory(Path.Combine(site, "design"));
        File.WriteAllText(Path.Combine(site, "design", "banner.psd"), "x");
        SettingsViewModel? shown = null;
        var dialogs = new ScriptedSettingsDialogs(settings => shown = settings);
        using var main = await MainViewModel.CreateAsync(Services(dialogs), settings: new AppSettings { LastLocalFolder = site });
        Assert.True(main.Local.Items.Single(row => row.Name == "Thumbs.db").IsExcluded);

        await main.OpenSettingsCommand.ExecuteAsync(null);
        Assert.NotNull(shown);
        await shown.Previewing;
        Assert.Equal(site, shown.PreviewFolder);
        Assert.Equal("1 件を送りません", shown.PreviewText);
        Assert.Equal("Thumbs.db", Assert.Single(shown.PreviewItems).Path);
        Assert.Equal(7, shown.ExclusionItems.Count);
        Assert.Equal("7 件のうち 7 件を使っています", shown.ExclusionSummary);
        Assert.Equal("このフォルダで 1 件", shown.ExclusionItems[0].HitText);
        Assert.False(shown.ExclusionItems[0].CanRemove);

        // よく足す名前を押すと足され、当てはめ直す
        shown.AddSuggestedPatternCommand.Execute("*.psd");
        await shown.Previewing;
        Assert.Equal("2 件を送りません", shown.PreviewText);
        Assert.Equal(@"design\banner.psd", shown.PreviewItems[0].Path);
        Assert.DoesNotContain("*.psd", shown.SuggestedPatterns);

        // 同じ名前や使えない名前は足せない
        shown.NewPattern = "*.PSD";
        shown.AddPatternCommand.Execute(null);
        Assert.Equal("「*.PSD」はもう入っています", shown.PatternError);
        shown.NewPattern = "*";
        shown.AddPatternCommand.Execute(null);
        Assert.Equal("* だけの名前は、すべてに当てはまるので使えません", shown.PatternError);

        // 使わない名前にすると、パソコン側の一覧の印も消える
        shown.ExclusionItems.Single(item => item.Pattern == "Thumbs.db").IsEnabled = false;
        await shown.Applying;
        Assert.False(main.Local.Items.Single(row => row.Name == "Thumbs.db").IsExcluded);

        // 自分で足した名前は消せる
        var added = shown.ExclusionItems.Single(item => item.Pattern == "*.psd");
        Assert.True(added.CanRemove);
        added.RemoveCommand.Execute(null);
        await shown.Applying;
        Assert.DoesNotContain(shown.ExclusionItems, item => item.Pattern == "*.psd");

        var saved = await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken);
        Assert.Equal(7, saved.Exclusions.Patterns.Count);
        Assert.False(saved.Exclusions.Patterns.Single(pattern => pattern.Pattern == "Thumbs.db").IsEnabled);

        // 最初の状態に戻せる
        shown.ResetExclusionsCommand.Execute(null);
        await shown.Applying;
        Assert.Equal(new ExclusionSettings(), main.Settings.Exclusions);
    }

    [Fact]
    public async Task Backup_choices_are_shown_and_applied_at_once()
    {
        SettingsViewModel? shown = null;
        var dialogs = new ScriptedSettingsDialogs(settings => shown = settings);
        using var main = await MainViewModel.CreateAsync(Services(dialogs), settings: new AppSettings { LastLocalFolder = MakeFolder("site") });

        await main.OpenSettingsCommand.ExecuteAsync(null);
        Assert.NotNull(shown);
        Assert.True(shown.BackupsEnabled);
        Assert.Equal(new[] { "7 日", "30 日", "90 日" }, shown.BackupKeepNames.ToArray());
        Assert.Equal(new[] { "500 MB", "1 GB", "5 GB" }, shown.BackupSizeNames.ToArray());
        Assert.Equal(1, shown.BackupKeepIndex);
        Assert.Equal(1, shown.BackupSizeIndex);
        Assert.Equal(Path.Combine(DataDirectory, "backups"), shown.BackupFolder);
        var backups = main.Backups!;
        Assert.Equal(TimeSpan.FromDays(30), backups.Limits.KeepFor);

        shown.BackupKeepIndex = 0;
        shown.BackupSizeIndex = 2;
        shown.BackupsEnabled = false;
        await shown.Applying;

        Assert.Equal(new BackupSettings { IsEnabled = false, KeepDays = 7, MaxTotalMegabytes = 5120 }, main.Settings.Backups);
        Assert.False(backups.Limits.IsEnabled);
        Assert.Equal(TimeSpan.FromDays(7), backups.Limits.KeepFor);
        Assert.Equal(5120L << 20, backups.Limits.MaxTotalBytes);
        Assert.Equal(new BackupSettings { IsEnabled = false, KeepDays = 7, MaxTotalMegabytes = 5120 },
            (await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken)).Backups);
    }

    [Fact]
    public async Task Notifications_and_taskbar_progress_are_on_by_default_and_can_be_turned_off()
    {
        SettingsViewModel? shown = null;
        var dialogs = new ScriptedSettingsDialogs(settings => shown = settings);
        using var main = await MainViewModel.CreateAsync(Services(dialogs), settings: new AppSettings { LastLocalFolder = MakeFolder("site") });

        await main.OpenSettingsCommand.ExecuteAsync(null);
        Assert.NotNull(shown);
        Assert.True(shown.NotifyWhenTransfersEnd);
        Assert.True(shown.ShowTaskbarProgress);
        Assert.True(shown.CheckForUpdates);

        shown.NotifyWhenTransfersEnd = false;
        shown.ShowTaskbarProgress = false;
        shown.CheckForUpdates = false;
        await shown.Applying;

        Assert.False(main.Settings.Updates.CheckForUpdates);

        var expected = new NotificationSettings { NotifyWhenTransfersEnd = false, ShowTaskbarProgress = false };
        Assert.Equal(expected, main.Settings.Notifications);
        Assert.Equal(expected, (await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken)).Notifications);
    }

    [Fact]
    public async Task Column_widths_are_shared_by_both_lists_and_saved()
    {
        using var main = await MainViewModel.CreateAsync(
            Services(), settings: new AppSettings { LastLocalFolder = MakeFolder("site"), Columns = new FileColumnWidths { Size = 90, Modified = 2000 } });

        Assert.Equal(90, main.Local.Columns.Size);
        Assert.Equal(90, main.Remote.Columns.Size);
        Assert.Equal(280, main.Remote.Columns.Modified); // 広すぎるものは直す

        var resized = new List<FileColumnWidths>();
        main.Remote.ColumnsResized += (_, widths) => resized.Add(widths);
        main.Remote.ResizeColumns(new FileColumnWidths { Size = 500, Modified = 150.4, Permissions = 10 });

        var expected = new FileColumnWidths { Size = 200, Modified = 150, Permissions = 60 };
        Assert.Equal(expected, Assert.Single(resized));
        Assert.Equal(expected, main.Local.Columns);
        Assert.Equal(expected, main.Remote.Columns);
        Assert.Equal(expected, (await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken)).Columns);
    }

    [Fact]
    public async Task Without_a_settings_screen_the_button_does_nothing()
    {
        using var main = await MainViewModel.CreateAsync(Services(), settings: new AppSettings { LastLocalFolder = MakeFolder("site") });

        Assert.False(main.OpenSettingsCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0.2.0+d400c6e9a1b2c3d4", "バージョン 0.2.0（d400c6e）")]
    [InlineData("0.2.0", "バージョン 0.2.0")]
    [InlineData("0.2.0+", "バージョン 0.2.0")]
    [InlineData(null, "バージョン不明")]
    public void The_version_is_shown_with_the_commit(string? informational, string expected) =>
        Assert.Equal(expected, SettingsViewModel.DescribeVersion(informational));
}

/// <summary>設定の画面のかわり。出されたら、決めておいた操作をする。</summary>
internal sealed class ScriptedSettingsDialogs(Action<SettingsViewModel> change) : ISettingsDialogs
{
    public int Shown { get; private set; }

    public Task ShowSettingsAsync(SettingsViewModel settings)
    {
        Shown++;
        change(settings);
        return Task.CompletedTask;
    }
}
