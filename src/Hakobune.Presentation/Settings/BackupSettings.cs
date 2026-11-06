using Hakobune.Core.Backups;

namespace Hakobune.Presentation.Settings;

/// <summary>上書き・削除の前の控えの設定。</summary>
public sealed record BackupSettings
{
    /// <summary>1 つのファイルの大きさの上限（これより大きいファイルは控えを取らない）。</summary>
    public const long MaxFileBytes = 100L << 20;

    /// <summary>残す日数の選択肢。</summary>
    public static IReadOnlyList<int> KeepDaysChoices { get; } = [7, 30, 90];

    /// <summary>全体の大きさの上限の選択肢（MB）。</summary>
    public static IReadOnlyList<int> TotalMegabytesChoices { get; } = [500, 1024, 5120];

    /// <summary>上書き・削除の前に控えを取るか。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>控えを残す日数。</summary>
    public int KeepDays { get; init; } = 30;

    /// <summary>控え全体の大きさの上限（MB）。超えたら古いものから消す。</summary>
    public int MaxTotalMegabytes { get; init; } = 1024;

    /// <summary>控えの置き場に渡す決まりにする（おかしな値は、使える範囲に収める）。</summary>
    public BackupLimits ToLimits()
    {
        var total = Math.Clamp(MaxTotalMegabytes, 100, 100_000) * (1L << 20);
        return new BackupLimits
        {
            IsEnabled = IsEnabled,
            KeepFor = TimeSpan.FromDays(Math.Clamp(KeepDays, 1, 3650)),
            MaxTotalBytes = total,
            MaxFileBytes = Math.Min(MaxFileBytes, total / 4),
        };
    }

    /// <summary>設定の画面に出す説明（「30 日・1 GB まで残します」）。</summary>
    public string Describe() => $"{KeepDays} 日・{DescribeMegabytes(MaxTotalMegabytes)}まで残します";

    /// <summary>大きさの選択肢の名前（「1 GB」）。</summary>
    public static string DescribeMegabytes(int megabytes) =>
        megabytes >= 1024 && megabytes % 1024 == 0 ? $"{megabytes / 1024} GB" : $"{megabytes} MB";
}
