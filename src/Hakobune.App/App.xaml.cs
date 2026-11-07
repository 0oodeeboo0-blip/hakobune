using System.Reflection;
using Hakobune.App.Services;
using Hakobune.Presentation;
using Hakobune.Presentation.Demo;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Transfers;
using Hakobune.Presentation.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hakobune.App;

public partial class App : Application
{
    private readonly WindowsNotifications _notifications = new();
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

    /// <summary>このアプリの版（例: 0.3.0。ビルドしたときのコミットは除く）。</summary>
    internal static Version CurrentVersion { get; } = UpdateCheck.ParseVersion(
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    /// <summary>上書き・削除の前の控えの置き場（大きくなるので、ローミングしない %LOCALAPPDATA%\Hakobune\backups）。</summary>
    internal static string BackupDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hakobune", "backups");

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
                    DataDirectory, new CredentialStore(), dialogs, dialogs, dialogs, dialogs, new RecycleBinFileOperations(), dialogs, dialogs,
                    BackupDirectory: BackupDirectory, BackupDialogs: dialogs, FfftpImport: dialogs,
                    Updates: Options.ScreenshotPath is null ? new GitHubUpdateSource(CurrentVersion) : null);
                var settings = await AppSettings.LoadAsync(services.SettingsFile);
                viewModel = await MainViewModel.CreateAsync(services, null, settings);
            }
            // Windows の通知: ふだんの起動と、見本の通知のときだけ使う（画面写真のときは Windows に登録しない）
            if (Options.ScreenshotPath is null || Options.ShowSampleNotification)
            {
                _notifications.Register(Path.Combine(AppContext.BaseDirectory, "Assets", "Hakobune.ico"));
                _notifications.Invoked += (_, _) => _window?.DispatcherQueue.TryEnqueue(() => _window?.BringToFront());
            }
            _window = new MainWindow(viewModel, Options, _notifications);
            _window.Closed += (_, _) => _notifications.Dispose();
            _window.Show();

            // 接続のときのダイアログを出せるよう、画面ができてから始める
            await _window.WhenLoaded;
            if (Options.ShowSampleNotification && TransferNotice.From(DemoData.SampleRunEnded, "ショップ（本番）") is { } sample)
            {
                _notifications.Show(sample.Title, sample.Text);
                // CI で、通知を出せたかを読めるように書き出す
                WriteNotificationStatus(_notifications.Status);
            }
            // 新しい版が出ていないか確かめる（見本の画面では、見本の知らせを出すだけ）
            if (Options.Demo is null)
                _ = viewModel.CheckForUpdatesAsync(CurrentVersion);
            else if (Options.ShowSampleUpdate)
                DemoData.ShowSampleUpdate(viewModel);
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
            // 取り直した場面は、光っているところを撮るので、撮る直前に取り直し、画面から撮る（動いている途中の色が写る）
            var refreshed = Options.Demo == DemoScenario.Refreshed && Options.Dialog is null;
            await _window.CaptureAndExitAsync(
                path,
                fromScreen: Options.Dialog is not null || refreshed,
                beforeCapture: refreshed ? () => DemoData.ShowUploadedAsync(viewModel) : null);
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
        if (Options.ShowBackups)
            viewModel.ShowBackupsCommand.Execute(null);
        if (Options.Dialog is { } dialog)
            return ShowDemoDialogAsync(dialog, viewModel, dialogs);
        if (Options.ConnectUrl is { } url)
            return ConnectAndUploadAsync(viewModel, url, Options.UploadPath);
        return Task.CompletedTask;
    }

    private static void WriteNotificationStatus(string status)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hakobune");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "notify-test.txt"), status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // 見本のダイアログを出す。閉じるまで終わらないので、出るまで少し待つ。出せなかったら記録する（CI で分かるように）
    private static async Task ShowDemoDialogAsync(DemoDialog dialog, MainViewModel viewModel, DialogService dialogs)
    {
        var showing = DemoDialogs.ShowAsync(dialog, dialogs, dialogs, dialogs, dialogs, dialogs, viewModel, dialogs, dialogs, dialogs);
        await Task.WhenAny(showing, Task.Delay(TimeSpan.FromSeconds(1)));
        if (showing.IsFaulted && showing.Exception is { } failure)
            CrashReport.Write(failure.GetBaseException());
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
