using System.ComponentModel;
using System.Runtime.InteropServices;
using Hakobune.App.Infrastructure;
using Hakobune.App.Views;
using Hakobune.Presentation;
using Hakobune.Presentation.Settings;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace Hakobune.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly MainView _view;
    private readonly AppOptions _options;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _readyToClose;

    // 最大化していないときの位置と大きさ（最大化して閉じても、次は元の大きさで開いてから最大化する）
    private RectInt32 _restoredBounds;
    private bool _maximizeOnShow;

    internal MainWindow(MainViewModel viewModel, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(options);
        _viewModel = viewModel;
        _options = options;

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

        // ライト・ダーク。タイトルバーのボタン（最小化・閉じる）の色も合わせる
        ApplyTheme();
        _view.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 前回の位置と大きさに戻す（画面写真のときと、その場所が今の画面に無いときは、真ん中に出す）
        if (options.ScreenshotPath is not null || viewModel.Settings.Window is not { } placement || !TryRestore(placement))
            PlaceWindow(options.Width, options.Height, center: options.ScreenshotPath is null);
        _restoredBounds = CurrentBounds();
        AppWindow.Changed += OnAppWindowChanged;

        _view.Loaded += (_, _) => _loaded.TrySetResult();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        };
    }

    /// <summary>初めて出す。前回最大化して閉じていれば、最大化する。</summary>
    internal void Show()
    {
        Activate();
        if (_maximizeOnShow && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Settings))
            ApplyTheme();
    }

    // 設定の画面の色にする。--theme の指定（画面写真）があれば、アプリ全体の色をそれで決めてあるので、設定より優先する
    private void ApplyTheme()
    {
        if (_options.Theme is null)
        {
            _view.RequestedTheme = _viewModel.Settings.Theme switch
            {
                ThemeChoice.Light => ElementTheme.Light,
                ThemeChoice.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        UpdateCaptionButtons();
    }

    // タイトルバーまで中身を広げると、最小化・最大化・閉じるのボタンの色は自分で決める（アプリの色を変えても自動では変わらない）
    private void UpdateCaptionButtons()
    {
        var dark = _view.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        var foreground = dark ? Colors.White : Colors.Black;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x72, 0x00, 0x00, 0x00);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
    }

    // 前回の位置に戻す。タイトルバーのあたりがどこかの画面に見えていなければ戻さない（つないでいた画面を外したときなど）。
    private bool TryRestore(WindowPlacement placement)
    {
        if (placement.Width < 480 || placement.Height < 360)
            return false;
        var bounds = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
        var titleBar = new RectInt32(placement.X + 40, placement.Y, Math.Max(1, placement.Width - 160), 40);
        if (DisplayArea.GetFromRect(titleBar, DisplayAreaFallback.None) is not { } display || !Overlaps(display.WorkArea, titleBar))
            return false;
        AppWindow.MoveAndResize(bounds);
        _maximizeOnShow = placement.IsMaximized;
        return true;
    }

    private static bool Overlaps(RectInt32 a, RectInt32 b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private RectInt32 CurrentBounds() =>
        new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange)
            && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            _restoredBounds = CurrentBounds();
    }

    // 閉じるときの位置と大きさ（最大化・最小化していれば、元の大きさ）
    private WindowPlacement CurrentPlacement()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        return new WindowPlacement(_restoredBounds.X, _restoredBounds.Y, _restoredBounds.Width, _restoredBounds.Height, maximized);
    }

    // 閉じる前に、転送が残っていれば止めてよいか聞き、送信待ちを保存してから閉じる
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_readyToClose)
            return;
        args.Cancel = true;
        try
        {
            if (!await _viewModel.PrepareToCloseAsync())
                return;
            // 画面写真のとき（CI）は、設定を書き換えない
            if (_options.ScreenshotPath is null)
                _viewModel.SaveSettings(CurrentPlacement());
        }
        catch (Exception ex)
        {
            CrashReport.Write(ex);
        }
        _readyToClose = true;
        Close();
    }

    /// <summary>画面ができあがった（ダイアログを出せるようになった）。</summary>
    internal Task WhenLoaded => _loaded.Task;

    /// <summary>
    /// 画面写真を撮って終わる。<paramref name="fromScreen"/> なら、ダイアログも写るように画面から撮る。
    /// <paramref name="beforeCapture"/> は、描き終わってから撮る直前にすること（見本の一覧の取り直しなど）。
    /// </summary>
    internal async Task CaptureAndExitAsync(string path, bool fromScreen, Func<Task>? beforeCapture = null)
    {
        try
        {
            // タイトルバーまで広げた分、中身は指定より大きくなるので、測って合わせる
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            FitContent(_options.Width, _options.Height);
            // 一覧の行やフォントが描き終わるのを待つ
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (beforeCapture is not null)
            {
                // 増えた行が描かれ、光り始めて少したったところで撮る
                await beforeCapture();
                await Task.Delay(TimeSpan.FromMilliseconds(700));
            }
            if (fromScreen)
                await Screenshot.SaveWindowAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), path);
            else
                await Screenshot.SaveAsync(_view, path);
        }
        catch (Exception ex)
        {
            CrashReport.Write(ex);
            Environment.Exit(1);
        }
        Application.Current.Exit();
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
