using System.Globalization;

namespace Hakobune.Presentation.Files;

/// <summary>
/// エクスプローラーと同じように、名前の中の数字を数として比べる（file2 が file10 より前に来る）。
/// 文字の部分は日本語の並び順で、大文字と小文字、全角と半角、ひらがなとカタカナを区別しない。
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    private const CompareOptions TextOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    private readonly CompareInfo _compareInfo;

    /// <summary>日本語の並び順で比べる。</summary>
    public NaturalStringComparer(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        _compareInfo = culture.CompareInfo;
    }

    /// <summary>日本語の並び順で比べるもの。</summary>
    public static NaturalStringComparer Instance { get; } = new(CultureInfo.GetCultureInfo("ja-JP"));

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = SkipZeros(x, i);
                var startY = SkipZeros(y, j);
                var endX = SkipDigits(x, startX);
                var endY = SkipDigits(y, startY);

                // 桁の多いほうが大きい。同じ桁数なら、文字の並びがそのまま数の大小になる。
                var lengthX = endX - startX;
                var lengthY = endY - startY;
                if (lengthX != lengthY)
                    return lengthX < lengthY ? -1 : 1;
                var digits = string.CompareOrdinal(x, startX, y, startY, lengthX);
                if (digits != 0)
                    return digits < 0 ? -1 : 1;

                i = endX;
                j = endY;
            }
            else
            {
                var endX = SkipText(x, i);
                var endY = SkipText(y, j);
                var text = _compareInfo.Compare(x, i, endX - i, y, j, endY - j, TextOptions);
                if (text != 0)
                    return text < 0 ? -1 : 1;

                i = endX;
                j = endY;
            }
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        if (rest != 0)
            return rest;

        // 見た目が同じ（例: 大文字と小文字だけ違う）なら、順番が毎回変わらないように文字の値で決める
        var ordinal = string.CompareOrdinal(x, y);
        return ordinal == 0 ? 0 : ordinal < 0 ? -1 : 1;
    }

    private static int SkipZeros(string text, int index)
    {
        while (index < text.Length - 1 && text[index] == '0' && char.IsAsciiDigit(text[index + 1]))
            index++;
        return index;
    }

    private static int SkipDigits(string text, int index)
    {
        while (index < text.Length && char.IsAsciiDigit(text[index]))
            index++;
        return index;
    }

    private static int SkipText(string text, int index)
    {
        while (index < text.Length && !char.IsAsciiDigit(text[index]))
            index++;
        return index;
    }
}
