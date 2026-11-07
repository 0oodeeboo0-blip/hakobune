namespace Hakobune.Presentation.Settings;

/// <summary>ファイルの一覧の列の幅（論理ピクセル）。名前の列は残りの幅を使う。パソコン側とサーバー側で同じにする。</summary>
public sealed record FileColumnWidths
{
    /// <summary>大きさの列。</summary>
    public double Size { get; init; } = 72;

    /// <summary>更新日時の列。</summary>
    public double Modified { get; init; } = 124;

    /// <summary>属性の列（サーバー側だけ）。</summary>
    public double Permissions { get; init; } = 84;

    /// <summary>狭すぎ・広すぎを直したもの。</summary>
    public FileColumnWidths Clamped() => new()
    {
        Size = Clamp(Size, 48, 200),
        Modified = Clamp(Modified, 80, 280),
        Permissions = Clamp(Permissions, 60, 180),
    };

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, min, max)) : min;
}
