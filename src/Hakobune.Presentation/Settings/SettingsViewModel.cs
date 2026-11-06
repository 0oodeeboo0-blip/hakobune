using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hakobune.Presentation.Settings;

/// <summary>設定の画面に出すもの（画面がダイアログで出す）。</summary>
public interface ISettingsDialogs
{
    /// <summary>設定の画面を出す。閉じるまで待つ。</summary>
    Task ShowSettingsAsync(SettingsViewModel settings);
}

/// <summary>設定の画面。変えるとすぐに画面へ反映して保存する（「保存」ボタンは置かない）。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly bool _ready;

    /// <summary><paramref name="dataDirectory"/> は設定などを保存するフォルダ（画面に出す）。</summary>
    public SettingsViewModel(MainViewModel main, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _main = main;
        DataDirectory = dataDirectory;

        var current = main.Settings;
        ThemeIndex = (int)current.Theme;
        ShowHiddenFiles = current.ShowHiddenFiles;
        UseCompactRows = current.UseCompactRows;
        _ready = true;
    }

    /// <summary>画面の色の選び方（並びは <see cref="ThemeChoice"/> と同じ）。</summary>
    public IReadOnlyList<string> ThemeNames { get; } = ["Windows の設定に合わせる", "ライト", "ダーク"];

    /// <summary>選んだ画面の色（<see cref="ThemeNames"/> の番号）。</summary>
    public int ThemeIndex
    {
        get;
        set
        {
            // 選び直しの途中で -1 が来ることがあるので、知らない番号は無視する
            if (value < 0 || value >= ThemeNames.Count || !SetProperty(ref field, value))
                return;
            Apply(_main.Settings with { Theme = (ThemeChoice)value });
        }
    }

    /// <summary>パソコン側で隠しファイルを出すか。</summary>
    public bool ShowHiddenFiles
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Apply(_main.Settings with { ShowHiddenFiles = value });
        }
    }

    /// <summary>一覧の行の高さを詰めるか（標準 34px、コンパクト 28px）。</summary>
    public bool UseCompactRows
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Apply(_main.Settings with { UseCompactRows = value });
        }
    }

    /// <summary>接続先・送信待ち・設定を保存するフォルダ。</summary>
    public string DataDirectory { get; }

    /// <summary>バージョン（例: バージョン 0.2.0（d400c6e））。</summary>
    public string VersionText { get; } = DescribeVersion(
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>変えた設定をすべて反映し終わるまでの Task（閉じたあとと試験で待つ）。</summary>
    public Task Applying { get; private set; } = Task.CompletedTask;

    /// <summary>「0.2.0+d400c6e…」（ビルドしたときのコミットつき）のような版の文字を、画面に出す形にする。</summary>
    public static string DescribeVersion(string? informationalVersion)
    {
        var text = informationalVersion?.Trim() ?? "";
        if (text.Length == 0)
            return "バージョン不明";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
            return $"バージョン {text}";
        var commit = text[(plus + 1)..];
        return commit.Length == 0
            ? $"バージョン {text[..plus]}"
            : $"バージョン {text[..plus]}（{commit[..Math.Min(7, commit.Length)]}）";
    }

    private void Apply(AppSettings settings)
    {
        if (_ready)
            Applying = Task.WhenAll(Applying, _main.ApplySettingsAsync(settings));
    }
}
