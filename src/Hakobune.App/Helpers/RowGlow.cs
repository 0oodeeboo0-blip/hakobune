using Hakobune.Presentation.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Hakobune.App.Helpers;

/// <summary>
/// 一覧の行を、うすい色でそっと光らせて消す（取り直しで増えた・変わった行の印）。
/// </summary>
/// <remarks>
/// 一覧の行の入れ物は、スクロールすると別の行に使い回される。そのたびに止めてから、光らせる行のときだけ始め直す
/// （ほかの行に光が残らないように）。
/// </remarks>
internal static class RowGlow
{
    // 色の濃さ: すぐに出て、半分すぎまでほとんどそのまま、あとはゆっくり消える（デザイン案の案B と同じ）
    private const double HoldOpacity = 0.83;
    private const double HoldPart = 0.55;

    /// <summary>
    /// <paramref name="glow"/> を光らせる。<paramref name="show"/> が false なら、消したままにする。
    /// 動きは <paramref name="glow"/> の Tag に置いて使い回す（行の入れ物ごとに 1 つ）。
    /// </summary>
    public static void Show(FrameworkElement glow, bool show)
    {
        ArgumentNullException.ThrowIfNull(glow);
        var storyboard = glow.Tag as Storyboard;
        // 止めると、色の濃さは元の 0（見えない）に戻る
        storyboard?.Stop();
        if (!show)
            return;
        if (storyboard is null)
        {
            storyboard = Create(glow);
            glow.Tag = storyboard;
        }
        storyboard.Begin();
    }

    private static Storyboard Create(UIElement glow)
    {
        var total = FilePaneViewModel.HighlightDuration;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1 });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(total * HoldPart),
            Value = HoldOpacity,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(total),
            Value = 0,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(fade, glow);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        return storyboard;
    }
}
