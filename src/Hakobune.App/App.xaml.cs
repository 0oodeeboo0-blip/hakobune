using Hakobune.App.Services;
using Hakobune.Presentation;
using Hakobune.Presentation.Demo;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hakobune.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        Options = AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        InitializeComponent();
        if (Options.Theme is { } theme)
            RequestedTheme = theme;
        UnhandledException += OnUnhandledException;
    }

    /// <summary>起動のときの指定。</summary>
    internal static AppOptions Options { get; private set; } = AppOptions.Default;

    /// <summary>接続先や信頼した鍵を保存するフォルダ（%APPDATA%\Hakobune）。</summary>
    internal static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hakobune");

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            var dialogs = new DialogService(
                DispatcherQueue.GetForCurrentThread(),
                () => _window?.Content?.XamlRoot,
                () => _window is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var viewModel = Options.Demo is { } scenario
                ? await DemoData.CreateAsync(scenario, SynchronizationContext.Current)
                : await MainViewModel.CreateAsync(new AppServices(DataDirectory, new CredentialStore(), dialogs, dialogs));
            _window = new MainWindow(viewModel, Options);
            _window.Activate();

            // 接続のときのダイアログを出せるよう、画面ができてから始める
            await _window.WhenLoaded;
            var ready = Start(viewModel, dialogs);
            if (Options.ScreenshotPath is not { } path)
                return;

            // 画面写真: 接続などが終わってから撮る（長くても 90 秒で撮る）
            await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(90)));
            if (Options.ConnectUrl is not null && !viewModel.IsConnected)
                CrashReport.WriteMessage($"接続できませんでした: {viewModel.Remote.MessageTitle} {viewModel.Remote.MessageReason}");
            await _window.CaptureAndExitAsync(path, fromScreen: Options.Dialog is not null);
        }
        catch (Exception ex)
        {
            CrashReport.Write(ex);
            Environment.Exit(1);
        }
    }

    // 起動のときの指定（--connect、--dialog）を始める。画面写真を撮ってよくなったら終わる Task を返す。
    private static Task Start(MainViewModel viewModel, DialogService dialogs)
    {
        if (Options.Dialog is { } dialog)
        {
            // ダイアログは閉じるまで終わらないので待たない。出るまで少し待つ。
            _ = DemoDialogs.ShowAsync(dialog, dialogs, dialogs);
            return Task.Delay(TimeSpan.FromSeconds(1));
        }
        if (Options.ConnectUrl is { } url)
            return viewModel.QuickConnectAsync(url);
        return Task.CompletedTask;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashReport.Write(e.Exception);
        if (Options.ScreenshotPath is not null)
            Environment.Exit(1);
    }
}
