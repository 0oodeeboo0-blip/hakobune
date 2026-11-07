using System.Globalization;
using System.Text;

namespace Hakobune.Core.Import;

/// <summary>
/// FFFTP の設定（レジストリの HKCU\Software\Sota\FFFTP の中身、ffftp.ini、「設定をファイルに保存」した .reg）。
/// キーは「FFFTP\Options\Host0」の形で、大文字・小文字は区別しない。値は文字（string）、数（int）、バイト列（byte[]）のどれか。
/// </summary>
public sealed class FfftpSettings
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    static FfftpSettings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>何も無いか。</summary>
    public bool IsEmpty => _keys.Count == 0;

    /// <summary>値を置く（レジストリから読むとき）。</summary>
    public void Set(string key, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        var normalized = Normalize(key);
        if (!_keys.TryGetValue(normalized, out var values))
            _keys[normalized] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        values[name] = value;
    }

    /// <summary>キーがあるか。</summary>
    public bool HasKey(string key) => _keys.ContainsKey(Normalize(key));

    /// <summary>値（無ければ null）。</summary>
    public object? Get(string key, string name) =>
        _keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name, out var value) ? value : null;

    /// <summary>文字の値（無ければ null）。</summary>
    public string? GetString(string key, string name) => Get(key, name) switch
    {
        string text => text,
        int number => number.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>数の値（無い・読めなければ null）。</summary>
    public int? GetInt(string key, string name) => Get(key, name) switch
    {
        int number => number,
        string text when int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    /// <summary>ffftp.ini か .reg を読む。どちらでもなければ空。</summary>
    public static FfftpSettings Load(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        // regedit の書き出しは UTF-16（BOM つき）。古い REGEDIT4 は Shift_JIS
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return ParseRegFile(Encoding.Unicode.GetString(content, 2, content.Length - 2));
        var start = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        var head = Encoding.ASCII.GetString(content, start, Math.Min(64, content.Length - start)).TrimStart();
        if (head.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase))
            return ParseRegFile(Encoding.UTF8.GetString(content, start, content.Length - start));
        if (head.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase))
            return ParseRegFile(Encoding.GetEncoding(932).GetString(content, start, content.Length - start));
        return ParseIni(content);
    }

    /// <summary>
    /// ffftp.ini を読む。値は ASCII に直して書いてある（\XX が 1 バイト、\\ が \）。元のバイト列は UTF-8
    /// （設定の版が 1980 より前なら Shift_JIS）。
    /// </summary>
    public static FfftpSettings ParseIni(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var raw = new List<(string Key, string Name, byte[] Value)>();
        string? section = null;
        foreach (var line in Encoding.Latin1.GetString(content).Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
                continue;
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                section = trimmed[1..^1].Trim();
                continue;
            }
            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (section is null || equals <= 0)
                continue;
            raw.Add((section, text[..equals].Trim(), Unescape(text[(equals + 1)..])));
        }

        var version = raw.Where(value => Normalize(value.Key).Equals("FFFTP", StringComparison.OrdinalIgnoreCase)
                && value.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))
            .Select(value => int.TryParse(Encoding.ASCII.GetString(value.Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : (int?)null)
            .FirstOrDefault();
        var encoding = version is < 1980 ? Encoding.GetEncoding(932) : new UTF8Encoding(false, throwOnInvalidBytes: false);
        var settings = new FfftpSettings();
        foreach (var (key, name, value) in raw)
            settings.Set(key, name, encoding.GetString(value));
        return settings;
    }

    /// <summary>regedit で書き出した .reg を読む（HKEY_CURRENT_USER\Software\Sota\FFFTP より下だけ）。</summary>
    public static FfftpSettings ParseRegFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var settings = new FfftpSettings();
        string? key = null;
        foreach (var line in JoinContinuedLines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';')
                continue;
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                key = RelativeKey(trimmed[1..^1]);
                continue;
            }
            if (key is null || trimmed[0] != '"' || ReadQuoted(trimmed, 0) is not { } name)
                continue;
            var rest = trimmed[name.End..].TrimStart();
            if (!rest.StartsWith('='))
                continue;
            if (ParseRegValue(rest[1..].Trim()) is { } value)
                settings.Set(key, name.Text, value);
        }
        return settings;
    }

    // 「HKEY_CURRENT_USER\Software\Sota\FFFTP\Options」→「FFFTP\Options」。FFFTP の下でなければ null
    private static string? RelativeKey(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, part => part.Equals("FFFTP", StringComparison.OrdinalIgnoreCase));
        return index < 0 || path.StartsWith('-') ? null : string.Join('\\', parts[index..]);
    }

    private static object? ParseRegValue(string value)
    {
        if (value.StartsWith('"'))
            return ReadQuoted(value, 0)?.Text;
        if (value.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(value[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)
                ? unchecked((int)number)
                : null;
        }
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !value.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
            return null;
        var bytes = ParseHex(value[(colon + 1)..]);
        var type = value[..colon];
        // hex(2) は展開する文字、hex(7) は複数の文字（UTF-16）。hex はバイト列
        if (type.Equals("hex(2)", StringComparison.OrdinalIgnoreCase))
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        if (type.Equals("hex(7)", StringComparison.OrdinalIgnoreCase))
            return null;
        return bytes;
    }

    private static byte[] ParseHex(string text)
    {
        var bytes = new List<byte>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                bytes.Add(value);
        }
        return [.. bytes];
    }

    // 「"名前"」を読む（\\ と \" を戻す）。読めなければ null
    private static (string Text, int End)? ReadQuoted(string text, int start)
    {
        if (start >= text.Length || text[start] != '"')
            return null;
        var builder = new StringBuilder();
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                builder.Append(text[++i]);
                continue;
            }
            if (c == '"')
                return (builder.ToString(), i + 1);
            builder.Append(c);
        }
        return null;
    }

    // 長い値は、行の終わりの \ で次の行へ続く
    private static IEnumerable<string> JoinContinuedLines(string text)
    {
        var builder = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.EndsWith('\\'))
            {
                builder.Append(line.AsSpan(0, line.Length - 1).Trim());
                continue;
            }
            builder.Append(builder.Length > 0 ? line.Trim() : line);
            yield return builder.ToString();
            builder.Clear();
        }
        if (builder.Length > 0)
            yield return builder.ToString();
    }

    private static byte[] Unescape(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] == '\\')
                {
                    bytes.Add((byte)'\\');
                    i++;
                    continue;
                }
                if (i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    bytes.Add(value);
                    i += 2;
                    continue;
                }
            }
            bytes.Add((byte)text[i]);
        }
        return [.. bytes];
    }

    private static string Normalize(string key) => key.Trim().Trim('\\');
}
