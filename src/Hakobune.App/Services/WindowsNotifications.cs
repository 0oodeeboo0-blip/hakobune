using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Hakobune.App.Services;

/// <summary>
/// Windows の通知（右下に出るもの）。登録できない環境では何も出さない。
/// 通知を押されたら <see cref="Invoked"/> で知らせる（ウインドウを前に出すため）。
/// </summary>
internal sealed class WindowsNotifications : IDisposable
{
    private bool _registered;

    /// <summary>いまの様子（登録できたか、できなければその理由。CI で見本の通知を確かめるときに書き出す）。</summary>
    public string Status { get; private set; } = "まだ登録していません";

    /// <summary>通知を押された。画面のスレッドとは限らない。</summary>
    public event EventHandler? Invoked;

    /// <summary>
    /// 通知を使えるようにする（起動のときに 1 回）。通知に出す名前は「Hakobune」、アイコンは <paramref name="iconPath"/>（.ico）。
    /// </summary>
    public void Register(string? iconPath)
    {
        if (_registered)
            return;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Status = "この Windows では通知を使えません（IsSupported が false）";
                return;
            }
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            try
            {
                // MSIX にしていないので、名前とアイコンを自分で渡す
                if (iconPath is not null && File.Exists(iconPath))
                    manager.Register("Hakobune", new Uri(iconPath));
                else
                    manager.Register();
            }
            catch (ArgumentException ex)
            {
                // アイコンを受け付けなかったときは、Windows に任せる（exe の名前とアイコン）
                Status = $"アイコンを受け付けませんでした（{ex.Message}）";
                manager.Register();
            }
            _registered = true;
            Status = $"登録しました（通知の設定: {manager.Setting}）";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
            or ArgumentException or TypeInitializationException or DllNotFoundException)
        {
            // 通知を出せない環境（古い Windows、通知を止めている会社のパソコンなど）では、何もしない
            _registered = false;
            Status = $"登録できませんでした（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    /// <summary>通知を出す。</summary>
    public void Show(string title, string text)
    {
        if (!_registered)
            return;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(text)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            Status = $"出しました（id {notification.Id}）";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Status = $"出せませんでした（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    public void Dispose()
    {
        if (!_registered)
            return;
        _registered = false;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Invoked?.Invoke(this, EventArgs.Empty);
}
