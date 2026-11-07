namespace Hakobune.Presentation.Settings;

/// <summary>知らせ方（Windows の通知と、タスクバーのボタン）。</summary>
public sealed record NotificationSettings
{
    /// <summary>
    /// 転送がひと区切りついたら（失敗や、接続できずに止まったときも）、Windows の通知で知らせる。
    /// Hakobune が前に出ているときは出さない（画面で分かるので）。
    /// </summary>
    public bool NotifyWhenTransfersEnd { get; init; } = true;

    /// <summary>転送中は、タスクバーのボタンに進み具合を出す（一時停止は黄、止まったときは赤）。</summary>
    public bool ShowTaskbarProgress { get; init; } = true;
}
