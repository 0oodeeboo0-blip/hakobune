using Hakobune.App.Services;
using Hakobune.Presentation;
using Hakobune.Presentation.Demo;
using Hakobune.Presentation.Settings;
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

    /// <summary>接続先・信頼した鍵・送信待ち・設定を保存するフォルダ（%APPDATA%\Hakobune）。</summary>
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
            MainViewModel viewModel;
            if (Options.Demo is { } scenario)
            {
                viewModel = await DemoData.CreateAsync(scenario, SynchronizationContext.Current);
            }
            else
            {
                var services = new AppServices(
                    DataDirectory, new CredentialStore(), dialogs, dialogs, dialogs, dialogs, new RecycleBinFileOperations(), dialogs);
                var settings = await AppSettings.LoadAsync(services.SettingsFile);
                viewModel = await MainViewModel.CreateAsync(services, null, settings);
            }
            _window = new MainWindow(viewModel, Options);
            _window.Show();

            // 接続のときのダイアログを出せるよう、画面ができてから始める
            await _window.WhenLoaded;
            var ready = Start(viewModel, dialogs);
            if (Options.ScreenshotPath is not { } path)
                return;

            // 画面写真: 接続や転送が終わってから撮る（長くても 4 分で撮る）
            await Task.WhenAny(ready, Task.Delay(TimeSpan.FromMinutes(4)));
            if (Options.ConnectUrl is not null && !viewModel.IsConnected)
                CrashReport.WriteMessage($"接続できませんでした: {viewModel.Remote.MessageTitle} {viewModel.Remote.MessageReason}");
            else if (Options.UploadPath is not null && (viewModel.Transfers.TotalCount == 0 || viewModel.PendingTransferCount > 0 || viewModel.Transfers.FailedCount > 0))
                CrashReport.WriteMessage(
                    $"アップロードが終わりませんでした: 全部 {viewModel.Transfers.TotalCount} 件、残り {viewModel.PendingTransferCount} 件、" +
                    $"失敗 {viewModel.Transfers.FailedCount} 件 {string.Join(" / ", viewModel.Transfers.Rows.Where(row => row.IsFailed).Select(row => row.FailureDetails))}");
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
        if (Options.ShowLog)
            viewModel.ShowLogCommand.Execute(null);
        if (Options.Dialog is { } dialog)
        {
            // ダイアログは閉じるまで終わらないので待たない。出るまで少し待つ。
            _ = DemoDialogs.ShowAsync(dialog, dialogs, dialogs, dialogs, dialogs, dialogs, viewModel);
            return Task.Delay(TimeSpan.FromSeconds(1));
        }
        if (Options.ConnectUrl is { } url)
            return ConnectAndUploadAsync(viewModel, url, Options.UploadPath);
        return Task.CompletedTask;
    }

    // 接続して、指定があれば送り、送り終わるまで待つ
    private static async Task ConnectAndUploadAsync(MainViewModel viewModel, string url, string? upload)
    {
        await viewModel.QuickConnectAsync(url);
        if (upload is null || !viewModel.IsConnected)
            return;
        await viewModel.UploadAsync([upload]);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (viewModel.PendingTransferCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        // 一覧の取り直しと、転送の一覧の表示が終わるのを待つ
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashReport.Write(e.Exception);
        if (Options.ScreenshotPath is not null)
            Environment.Exit(1);
    }
}
