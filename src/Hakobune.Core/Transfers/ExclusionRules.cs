using System.Text;
using System.Text.RegularExpressions;

namespace Hakobune.Core.Transfers;

/// <summary>
/// 送らない名前の決まり（Thumbs.db、*.tmp、.git/ など）。大文字と小文字は区別しない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>*</c> は / 以外のどんな文字の並びにも、<c>?</c> は / 以外の 1 文字に当てはまる。</item>
/// <item>最後が / の名前（<c>backup/</c>）はフォルダだけに当てはまる。フォルダが当てはまれば、中身も全部送らない。</item>
/// <item>/ を含まない名前は、どの深さの名前とも比べる。途中に / がある名前（<c>assets/cache</c>）と / で始まる名前（<c>/config.php</c>）は、
/// 送るもとのフォルダからの位置と比べる。</item>
/// </list>
/// </remarks>
public sealed class ExclusionRules
{
    private readonly Rule[] _rules;

    /// <summary>名前の並びから作る。空の名前・重なり・使えない名前は除く。</summary>
    public ExclusionRules(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var rules = new List<Rule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in patterns)
        {
            var pattern = Normalize(raw);
            if (Validate(pattern) is null && seen.Add(pattern))
                rules.Add(Rule.Create(pattern));
        }
        _rules = [.. rules];
        Patterns = [.. rules.Select(rule => rule.Pattern)];
    }

    /// <summary>何も除かない決まり。</summary>
    public static ExclusionRules None { get; } = new([]);

    /// <summary>使っている名前（整えたもの）。</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>何も除かないか。</summary>
    public bool IsEmpty => _rules.Length == 0;

    /// <summary>
    /// <paramref name="relativePath"/>（送るもとのフォルダからの位置。区切りは / か \）が送らないものなら、当てはまった名前を返す。
    /// 途中のフォルダが当てはまるときも返す。
    /// </summary>
    public string? Match(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (_rules.Length == 0)
            return null;
        var segments = Split(relativePath);
        // 上のフォルダから順に見る（途中のフォルダが当てはまれば、中身も送らない）
        for (var depth = 1; depth <= segments.Length; depth++)
        {
            var directory = depth < segments.Length || isDirectory;
            if (MatchAt(segments, depth, directory) is { } pattern)
                return pattern;
        }
        return null;
    }

    /// <summary>
    /// フォルダをたどりながら確かめるとき用。上のフォルダはもう確かめてあるものとして、<paramref name="relativePath"/> の最後の名前だけを見る。
    /// </summary>
    public string? MatchLast(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (_rules.Length == 0)
            return null;
        var segments = Split(relativePath);
        return segments.Length == 0 ? null : MatchAt(segments, segments.Length, isDirectory);
    }

    /// <summary>
    /// 名前だけで確かめる（パソコン側の一覧の印に使う。位置で決まる名前は、送るもとのフォルダが分からないので見ない）。
    /// </summary>
    public string? MatchName(string name, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var rule in _rules)
        {
            if (!rule.ByPath && rule.Matches(name, name, isDirectory))
                return rule.Pattern;
        }
        return null;
    }

    /// <summary>送らないものか。</summary>
    public bool IsExcluded(string relativePath, bool isDirectory) => Match(relativePath, isDirectory) is not null;

    /// <summary>入力された名前を整える（前後の空白を取り、\ を / にする）。</summary>
    public static string Normalize(string? pattern) => (pattern ?? "").Trim().Replace('\\', '/');

    /// <summary>名前として使えなければ、その理由（画面に出す）。使えれば null。</summary>
    public static string? Validate(string? pattern)
    {
        var text = Normalize(pattern);
        if (text.Length == 0)
            return "名前を入れてください";
        if (text.Any(char.IsControl))
            return "使えない文字が入っています";
        var body = text.Trim('/');
        if (body.Length == 0)
            return "名前を入れてください";
        if (body.Split('/').Any(part => part.Length == 0))
            return "/ が続いています";
        if (body.All(c => c is '*' or '/'))
            return "* だけの名前は、すべてに当てはまるので使えません";
        return null;
    }

    private static string[] Split(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private string? MatchAt(string[] segments, int depth, bool isDirectory)
    {
        var name = segments[depth - 1];
        var path = depth == 1 ? name : string.Join('/', segments, 0, depth);
        foreach (var rule in _rules)
        {
            if (rule.Matches(path, name, isDirectory))
                return rule.Pattern;
        }
        return null;
    }

    private sealed class Rule
    {
        private readonly Regex _regex;
        private readonly bool _directoryOnly;

        private Rule(string pattern, Regex regex, bool directoryOnly, bool byPath)
        {
            Pattern = pattern;
            _regex = regex;
            _directoryOnly = directoryOnly;
            ByPath = byPath;
        }

        public string Pattern { get; }

        /// <summary>送るもとのフォルダからの位置と比べるか（名前だけでは比べない）。</summary>
        public bool ByPath { get; }

        public static Rule Create(string pattern)
        {
            var directoryOnly = pattern.EndsWith('/');
            var body = pattern.Trim('/');
            var byPath = pattern.StartsWith('/') || body.Contains('/', StringComparison.Ordinal);
            return new Rule(pattern, ToRegex(body), directoryOnly, byPath);
        }

        public bool Matches(string path, string name, bool isDirectory)
        {
            if (_directoryOnly && !isDirectory)
                return false;
            return _regex.IsMatch(ByPath ? path : name);
        }

        private static Regex ToRegex(string glob)
        {
            var builder = new StringBuilder("^");
            foreach (var c in glob)
            {
                builder.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
            builder.Append('$');
            return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
