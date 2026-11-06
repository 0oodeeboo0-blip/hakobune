using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Settings;

/// <summary>送らない名前 1 つ。</summary>
public sealed record ExclusionPattern
{
    /// <summary>名前（書き方は <see cref="ExclusionRules"/>）。</summary>
    public string Pattern { get; init; } = "";

    /// <summary>使うか。</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// 送らないファイルの設定。アップロードと同期のとき、当てはまるファイルやフォルダは送らない
/// （同期でサーバーからも消すときも、当てはまるものは消さない）。
/// </summary>
public sealed record ExclusionSettings
{
    // はじめから入っている名前と、その説明（この順に並べる）
    private static readonly (string Pattern, string Description)[] BuiltIns =
    [
        ("Thumbs.db", "Windows が作る縮小画像の控え"),
        ("desktop.ini", "フォルダの表示の設定"),
        (".DS_Store", "Mac が作るフォルダの設定"),
        (".git/", "Git の管理フォルダ"),
        ("node_modules/", "開発用の部品のフォルダ"),
        ("~$*", "Office で開いている間の作業ファイル"),
        ("*.tmp", "一時ファイル"),
    ];

    private static readonly Dictionary<string, string> BuiltInDescriptions =
        BuiltIns.ToDictionary(item => item.Pattern, item => item.Description, StringComparer.OrdinalIgnoreCase);

    /// <summary>はじめから入っている名前。</summary>
    public static IReadOnlyList<ExclusionPattern> Defaults { get; } =
        [.. BuiltIns.Select(item => new ExclusionPattern { Pattern = item.Pattern })];

    /// <summary>よく足す名前（設定の画面で、押すだけで足せる）。</summary>
    public static IReadOnlyList<string> Suggestions { get; } = ["*.psd", "*.ai", "backup/", ".vscode/"];

    /// <summary>送らないファイルを使うか（切ると、接続先ごとの名前も含めて何も除かない）。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>名前の並び。</summary>
    public IReadOnlyList<ExclusionPattern> Patterns { get; init; } = Defaults;

    /// <summary>はじめから入っている名前か（消せない。使うかどうかだけ選べる）。</summary>
    public static bool IsBuiltIn(string pattern) => BuiltInDescriptions.ContainsKey(ExclusionRules.Normalize(pattern));

    /// <summary>名前の説明。</summary>
    public static string Describe(string pattern) =>
        BuiltInDescriptions.TryGetValue(ExclusionRules.Normalize(pattern), out var description) ? description : "自分で足したもの";

    /// <summary>名前の並びも中身で比べる（保存して読み直したものと同じと分かるように）。</summary>
    public bool Equals(ExclusionSettings? other) =>
        other is not null && IsEnabled == other.IsEnabled && Patterns.SequenceEqual(other.Patterns);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsEnabled, Patterns.Count);

    /// <summary>実際に使う決まり。<paramref name="extra"/> は接続先ごとの名前。</summary>
    public ExclusionRules ToRules(IEnumerable<string>? extra = null)
    {
        if (!IsEnabled)
            return ExclusionRules.None;
        var patterns = Patterns.Where(pattern => pattern.IsEnabled).Select(pattern => pattern.Pattern);
        return new ExclusionRules(extra is null ? patterns : patterns.Concat(extra));
    }
}
