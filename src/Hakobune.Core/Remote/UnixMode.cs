using System.Globalization;

namespace Hakobune.Core.Remote;

/// <summary>Unix のパーミッション（rwxr-xr-x、755 など）の変換。</summary>
public static class UnixMode
{
    /// <summary>
    /// 8 進で書いた数字を、そのまま 10 進の数として返す（0755 を表す 493 なら 755）。FTP の SITE CHMOD や SSH.NET が使う形。
    /// </summary>
    public static int ToOctalDigits(int mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, 0xFFF);
        return int.Parse(Convert.ToString(mode, 8), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>「644」のような 8 進の文字から読む。読めなければ null。</summary>
    public static int? Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length is 0 or > 4 || trimmed.Any(c => c is < '0' or > '7'))
            return null;
        return Convert.ToInt32(trimmed, 8);
    }

    /// <summary>「644」のような 8 進の文字にする（特別な属性がなければ 3 けた）。</summary>
    public static string Format(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0');

    /// <summary>
    /// 「-rw-r--r--」「rwxr-xr-x」「0644」のような属性の文字から読む。読めなければ null。
    /// </summary>
    public static int? FromText(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (Parse(trimmed) is { } octal)
            return octal;
        // 先頭の種類（d、-、l）は飛ばす
        if (trimmed.Length == 10)
            trimmed = trimmed[1..];
        if (trimmed.Length != 9)
            return null;
        var mode = 0;
        for (var i = 0; i < 9; i++)
        {
            var expected = "rwx"[i % 3];
            var c = trimmed[i];
            if (c == expected || (i % 3 == 2 && c is 's' or 't'))
                mode |= 1 << (8 - i);
            else if (c is not ('-' or 'S' or 'T'))
                return null;
        }
        return mode;
    }
}
