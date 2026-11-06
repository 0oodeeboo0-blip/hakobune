using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Settings;
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
        var settings = new AppSettings { LastLocalFolder = folder, IsSidebarOpen = false, IsTransferListExpanded = false, UseCompactRows = true };

        using var main = await MainViewModel.CreateAsync(Services(), settings: settings);

        Assert.Equal(folder, main.Local.CurrentPath);
        Assert.False(main.IsSidebarOpen);
        Assert.False(main.Transfers.IsExpanded);
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

        main.SaveSettings(new WindowPlacement(100, 50, 1300, 860, IsMaximized: false));

        var saved = await AppSettings.LoadAsync(SettingsFile, TestContext.Current.CancellationToken);
        Assert.Equal(folder, saved.LastLocalFolder);
        Assert.False(saved.IsSidebarOpen);
        Assert.False(saved.IsTransferListExpanded);
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
