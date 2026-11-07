using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;

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
    // 当てはめてみた結果を、画面に出す数の上限
    private const int MaxPreviewItems = 200;

    private readonly MainViewModel _main;
    private readonly bool _ready;
    private CancellationTokenSource? _previewCancel;

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

        NotifyWhenTransfersEnd = current.Notifications.NotifyWhenTransfersEnd;
        ShowTaskbarProgress = current.Notifications.ShowTaskbarProgress;
        CheckForUpdates = current.Updates.CheckForUpdates;

        BackupsEnabled = current.Backups.IsEnabled;
        BackupKeepIndex = IndexOf(BackupSettings.KeepDaysChoices, current.Backups.KeepDays, fallback: 1);
        BackupSizeIndex = IndexOf(BackupSettings.TotalMegabytesChoices, current.Backups.MaxTotalMegabytes, fallback: 1);
        BackupFolder = main.Backups?.Root ?? "";

        ExclusionsEnabled = current.Exclusions.IsEnabled;
        foreach (var pattern in current.Exclusions.Patterns)
            ExclusionItems.Add(NewItem(pattern.Pattern, pattern.IsEnabled));
        AddPatternCommand = new RelayCommand(() => AddPattern(NewPattern));
        AddSuggestedPatternCommand = new RelayCommand<string>(pattern =>
        {
            if (pattern is not null)
                AddPattern(pattern);
        });
        ResetExclusionsCommand = new RelayCommand(ResetExclusions);
        // パソコン側でいま開いているフォルダに当てはめて見せる（ドライブの一覧のときは見せない）
        PreviewFolder = main.Local.HasLoaded && main.Local.Source is not null ? main.Local.CurrentPath : "";
        UpdateExclusionSummary();
        _ready = true;
        StartPreview();
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

    // ---- お知らせ ----

    /// <summary>転送がひと区切りついたら、Windows の通知で知らせるか。</summary>
    public bool NotifyWhenTransfersEnd
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Apply(_main.Settings with { Notifications = _main.Settings.Notifications with { NotifyWhenTransfersEnd = value } });
        }
    }

    /// <summary>転送中は、タスクバーのボタンに進み具合を出すか。</summary>
    public bool ShowTaskbarProgress
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Apply(_main.Settings with { Notifications = _main.Settings.Notifications with { ShowTaskbarProgress = value } });
        }
    }

    /// <summary>新しい版が出たら知らせるか。</summary>
    public bool CheckForUpdates
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Apply(_main.Settings with { Updates = _main.Settings.Updates with { CheckForUpdates = value } });
        }
    }

    // ---- 控え ----

    /// <summary>上書き・削除の前に控えを取るか。</summary>
    public bool BackupsEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Apply(_main.Settings with { Backups = _main.Settings.Backups with { IsEnabled = value } });
        }
    }

    /// <summary>残す日数の選び方。</summary>
    public IReadOnlyList<string> BackupKeepNames { get; } = [.. BackupSettings.KeepDaysChoices.Select(days => $"{days} 日")];

    /// <summary>選んだ残す日数（<see cref="BackupKeepNames"/> の番号）。</summary>
    public int BackupKeepIndex
    {
        get;
        set
        {
            if (value < 0 || value >= BackupSettings.KeepDaysChoices.Count || !SetProperty(ref field, value))
                return;
            Apply(_main.Settings with { Backups = _main.Settings.Backups with { KeepDays = BackupSettings.KeepDaysChoices[value] } });
        }
    }

    /// <summary>全体の大きさの上限の選び方。</summary>
    public IReadOnlyList<string> BackupSizeNames { get; } = [.. BackupSettings.TotalMegabytesChoices.Select(BackupSettings.DescribeMegabytes)];

    /// <summary>選んだ全体の大きさの上限（<see cref="BackupSizeNames"/> の番号）。</summary>
    public int BackupSizeIndex
    {
        get;
        set
        {
            if (value < 0 || value >= BackupSettings.TotalMegabytesChoices.Count || !SetProperty(ref field, value))
                return;
            Apply(_main.Settings with { Backups = _main.Settings.Backups with { MaxTotalMegabytes = BackupSettings.TotalMegabytesChoices[value] } });
        }
    }

    /// <summary>控えの置き場（エクスプローラーで開くため）。</summary>
    public string BackupFolder { get; }

    /// <summary>控えの置き場があるか（見本の画面では無い）。</summary>
    public bool HasBackupFolder => BackupFolder.Length > 0;

    private static int IndexOf(IReadOnlyList<int> choices, int value, int fallback)
    {
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i] == value)
                return i;
        }
        return fallback;
    }

    // ---- 送らないファイル ----

    /// <summary>送らないファイルを使うか。</summary>
    public bool ExclusionsEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                SaveExclusions();
        }
    }

    /// <summary>送らない名前の行。</summary>
    public ObservableCollection<ExclusionItemViewModel> ExclusionItems { get; } = [];

    /// <summary>「7 件のうち 7 件を使っています」。</summary>
    public string ExclusionSummary { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>足す名前の入力。</summary>
    public string NewPattern
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
                PatternError = "";
        }
    } = "";

    /// <summary>足せなかった理由（なければ空）。</summary>
    public string PatternError
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(HasPatternError));
        }
    } = "";

    /// <summary>足せなかった理由を出すか。</summary>
    public bool HasPatternError => PatternError.Length > 0;

    /// <summary>まだ入っていない、よく足す名前（押すだけで足せる）。</summary>
    public ObservableCollection<string> SuggestedPatterns { get; } = [];

    /// <summary>入力した名前を足す。</summary>
    public IRelayCommand AddPatternCommand { get; }

    /// <summary>よく足す名前を足す。</summary>
    public IRelayCommand<string> AddSuggestedPatternCommand { get; }

    /// <summary>はじめの状態に戻す。</summary>
    public IRelayCommand ResetExclusionsCommand { get; }

    /// <summary>当てはめてみるフォルダ（パソコン側でいま開いているフォルダ。無ければ空）。</summary>
    public string PreviewFolder { get; }

    /// <summary>当てはめた結果を出すか。</summary>
    public bool HasPreviewFolder => PreviewFolder.Length > 0;

    /// <summary>当てはめた結果（「14 件を送りません」など）。</summary>
    public string PreviewText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>当てはまったもの（多いときは先頭から 200 件）。</summary>
    public ObservableCollection<ExclusionMatch> PreviewItems { get; } = [];

    /// <summary>当てはめが終わるまでの Task（試験で待つ）。</summary>
    public Task Previewing { get; private set; } = Task.CompletedTask;

    /// <summary>開いたときに「送らないファイル」のところまで送るか（見本の画面写真用）。</summary>
    public bool OpensAtExclusions { get; init; }

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

    private ExclusionItemViewModel NewItem(string pattern, bool enabled) => new(pattern, enabled, SaveExclusions, RemoveItem);

    private void AddPattern(string text)
    {
        var pattern = ExclusionRules.Normalize(text);
        if (ExclusionRules.Validate(pattern) is { } problem)
        {
            PatternError = problem;
            return;
        }
        if (ExclusionItems.Any(item => string.Equals(item.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            PatternError = $"「{pattern}」はもう入っています";
            return;
        }
        ExclusionItems.Add(NewItem(pattern, enabled: true));
        NewPattern = "";
        SaveExclusions();
    }

    private void RemoveItem(ExclusionItemViewModel item)
    {
        if (item.IsBuiltIn || !ExclusionItems.Remove(item))
            return;
        SaveExclusions();
    }

    private void ResetExclusions()
    {
        ExclusionItems.Clear();
        foreach (var pattern in ExclusionSettings.Defaults)
            ExclusionItems.Add(NewItem(pattern.Pattern, pattern.IsEnabled));
        NewPattern = "";
        PatternError = "";
        if (ExclusionsEnabled)
            SaveExclusions();
        else
            ExclusionsEnabled = true;
    }

    // 画面で変えた送らないファイルを、すぐに反映して保存し、当てはめ直す
    private void SaveExclusions()
    {
        UpdateExclusionSummary();
        if (!_ready)
            return;
        var exclusions = new ExclusionSettings
        {
            IsEnabled = ExclusionsEnabled,
            Patterns = [.. ExclusionItems.Select(item => new ExclusionPattern { Pattern = item.Pattern, IsEnabled = item.IsEnabled })],
        };
        Apply(_main.Settings with { Exclusions = exclusions });
        StartPreview();
    }

    private void UpdateExclusionSummary()
    {
        var used = ExclusionItems.Count(item => item.IsEnabled);
        ExclusionSummary = ExclusionsEnabled ? $"{ExclusionItems.Count} 件のうち {used} 件を使っています" : "使っていません";
        var taken = ExclusionItems.Select(item => item.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = ExclusionSettings.Suggestions.Where(pattern => !taken.Contains(pattern)).ToList();
        if (SuggestedPatterns.SequenceEqual(suggestions))
            return;
        SuggestedPatterns.Clear();
        foreach (var pattern in suggestions)
            SuggestedPatterns.Add(pattern);
    }

    private void StartPreview()
    {
        _previewCancel?.Cancel();
        if (!HasPreviewFolder || _main.Local.Source is not { } source)
            return;
        var cancel = new CancellationTokenSource();
        _previewCancel = cancel;
        PreviewText = "数えています…";
        Previewing = PreviewAsync(source, _main.Settings.Exclusions.ToRules(), cancel.Token);
    }

    private async Task PreviewAsync(IFileSource source, ExclusionRules rules, CancellationToken cancellationToken)
    {
        ExclusionPreviewResult result;
        try
        {
            result = await ExclusionPreview.ScanAsync(source, PreviewFolder, rules, ExclusionPreview.MaxEntries, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested)
            return;

        PreviewItems.Clear();
        foreach (var match in result.Matches.Take(MaxPreviewItems))
            PreviewItems.Add(match);
        var count = result.Matches.Count;
        PreviewText = count == 0 ? "当てはまるものはありません。すべて送ります" : $"{count:N0} 件を送りません";
        if (result.Truncated)
            PreviewText += $"（多いので、最初の {ExclusionPreview.MaxEntries:N0} 件だけ見ました）";
        foreach (var item in ExclusionItems)
        {
            var hits = result.Matches.Count(match => string.Equals(match.Pattern, item.Pattern, StringComparison.OrdinalIgnoreCase));
            item.HitText = hits > 0 ? $"このフォルダで {hits:N0} 件" : "";
        }
    }
}
