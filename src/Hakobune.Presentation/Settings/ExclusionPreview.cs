using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;

namespace Hakobune.Presentation.Settings;

/// <summary>送らないファイルの決まりに当てはまったもの 1 つ。</summary>
/// <param name="Path">当てはめたフォルダからの位置（区切りは \）。</param>
/// <param name="Pattern">当てはまった名前。</param>
/// <param name="IsDirectory">フォルダか（中身もまとめて送らない）。</param>
public sealed record ExclusionMatch(string Path, string Pattern, bool IsDirectory)
{
    /// <summary>画面に出す位置（フォルダは「（フォルダ）」を付ける）。</summary>
    public string DisplayPath => IsDirectory ? Path + "（フォルダ）" : Path;
}

/// <summary>当てはめてみた結果。</summary>
/// <param name="Matches">当てはまったもの（位置の順）。</param>
/// <param name="Truncated">多すぎて途中までしか見なかったか。</param>
public sealed record ExclusionPreviewResult(IReadOnlyList<ExclusionMatch> Matches, bool Truncated);

/// <summary>
/// 送らないファイルの決まりを、パソコンのフォルダに当てはめてみる（設定の画面で、何が除かれるかを見せる）。
/// 送るときと同じく、当てはまったフォルダの中は見ない。
/// </summary>
public static class ExclusionPreview
{
    /// <summary>見る項目の数の上限（大きなフォルダで待たせないため）。</summary>
    public const int MaxEntries = 20_000;

    /// <summary><paramref name="folder"/> の中をたどって、当てはまるものを集める。</summary>
    public static async Task<ExclusionPreviewResult> ScanAsync(
        IFileSource source, string folder, ExclusionRules rules, int maxEntries = MaxEntries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(rules);
        var matches = new List<ExclusionMatch>();
        if (rules.IsEmpty)
            return new ExclusionPreviewResult(matches, Truncated: false);

        var seen = 0;
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((folder, ""));
        while (pending.TryPop(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listing = await source.ListAsync(next.Path, cancellationToken).ConfigureAwait(false);
            if (!listing.Succeeded)
                continue;
            foreach (var entry in listing.Entries)
            {
                if (++seen > maxEntries)
                    return new ExclusionPreviewResult(Sorted(matches), Truncated: true);
                if (entry.Type == FileEntryType.Drive)
                    continue;
                var relative = next.Relative.Length == 0 ? entry.Name : next.Relative + "/" + entry.Name;
                if (rules.MatchLast(relative, entry.IsDirectory) is { } pattern)
                {
                    matches.Add(new ExclusionMatch(relative.Replace('/', '\\'), pattern, entry.IsDirectory));
                    continue;
                }
                if (entry.Type == FileEntryType.Directory)
                    pending.Push((entry.FullPath, relative));
            }
        }
        return new ExclusionPreviewResult(Sorted(matches), Truncated: false);
    }

    private static List<ExclusionMatch> Sorted(List<ExclusionMatch> matches) =>
        [.. matches.OrderBy(match => match.Path, NaturalStringComparer.Instance)];
}

/// <summary>設定の画面の、送らない名前 1 行。</summary>
public sealed class ExclusionItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly bool _ready;

    internal ExclusionItemViewModel(string pattern, bool isEnabled, Action changed, Action<ExclusionItemViewModel> remove)
    {
        Pattern = pattern;
        _changed = changed;
        IsEnabled = isEnabled;
        RemoveCommand = new RelayCommand(() => remove(this), () => CanRemove);
        _ready = true;
    }

    /// <summary>名前。</summary>
    public string Pattern { get; }

    /// <summary>説明（はじめから入っている名前なら、何のファイルか）。</summary>
    public string Description => ExclusionSettings.Describe(Pattern);

    /// <summary>はじめから入っている名前か（消せない。使うかどうかだけ選べる）。</summary>
    public bool IsBuiltIn => ExclusionSettings.IsBuiltIn(Pattern);

    /// <summary>消せるか（自分で足した名前）。</summary>
    public bool CanRemove => !IsBuiltIn;

    /// <summary>消すボタンの読み上げ。</summary>
    public string RemoveLabel => $"「{Pattern}」を消す";

    /// <summary>使うか。</summary>
    public bool IsEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && _ready)
                _changed();
        }
    }

    /// <summary>当てはめてみたフォルダで、いくつ当てはまったか（「このフォルダで 6 件」）。</summary>
    public string HitText { get; internal set => SetProperty(ref field, value); } = "";

    /// <summary>消す。</summary>
    public IRelayCommand RemoveCommand { get; }
}
