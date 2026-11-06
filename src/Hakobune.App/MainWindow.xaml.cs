using System.Runtime.InteropServices;
using Hakobune.App.Infrastructure;
using Hakobune.App.Views;
using Hakobune.Presentation;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Hakobune.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly MainView _view;

    internal MainWindow(MainViewModel viewModel, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(options);
        _viewModel = viewModel;

        InitializeComponent();
        Title = "Hakobune";

        _view = new MainView(viewModel);
        Content = _view;

        // タイトルバーまで中身を広げ、Windows 11 らしい見た目にする
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_view.TitleBarElement);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetIcon();

        // Mica が使えないとき（Windows 10 など）と画面写真のときは、無地の背景にする
        if (options.ScreenshotPath is null && MicaController.IsSupported())
            _view.UseTransparentBackground();

        PlaceWindow(options.Width, options.Height, center: options.ScreenshotPath is null);

        if (options.ScreenshotPath is { } path)
            _view.Loaded += async (_, _) => await CaptureAndExitAsync(path, options.Width, options.Height);

        Closed += (_, _) => _viewModel.Dispose();
    }

    private void SetIcon()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Hakobune.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
    }

    // 中身の大きさ（論理ピクセル）でウインドウの大きさを決める。画面より大きければ縮める。
    private void PlaceWindow(int width, int height, bool center)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (scale <= 0)
            scale = 1;
        var size = new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        if (center)
        {
            size.Width = Math.Min(size.Width, (int)(area.Width * 0.95));
            size.Height = Math.Min(size.Height, (int)(area.Height * 0.95));
        }
        AppWindow.ResizeClient(size);

        var outer = AppWindow.Size;
        var x = center ? area.X + Math.Max(0, (area.Width - outer.Width) / 2) : area.X;
        var y = center ? area.Y + Math.Max(0, (area.Height - outer.Height) / 2) : area.Y;
        AppWindow.Move(new PointInt32(x, y));
    }

    private async Task CaptureAndExitAsync(string path, int width, int height)
    {
        try
        {
            // タイトルバーまで広げた分、中身は指定より大きくなるので、測って合わせる
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            FitContent(width, height);
            // 一覧の行やフォントが描き終わるのを待つ
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Screenshot.SaveAsync(_view, path);
        }
        catch (Exception ex)
        {
            CrashReport.Write(ex);
            Environment.Exit(1);
        }
        Application.Current.Exit();
    }

    // 中身（MainView）がちょうど width x height（論理ピクセル）になるよう、ウインドウの大きさを直す
    private void FitContent(int width, int height)
    {
        var scale = _view.XamlRoot?.RasterizationScale ?? 1d;
        var dx = (int)Math.Round((width - _view.ActualWidth) * scale);
        var dy = (int)Math.Round((height - _view.ActualHeight) * scale);
        if (dx == 0 && dy == 0)
            return;
        var size = AppWindow.Size;
        AppWindow.Resize(new SizeInt32(size.Width + dx, size.Height + dy));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
