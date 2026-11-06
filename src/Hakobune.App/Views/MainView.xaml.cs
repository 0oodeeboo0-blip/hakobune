using Hakobune.Presentation;
using Hakobune.Presentation.Connections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Hakobune.App.Views;

/// <summary>メイン画面の中身（タイトルバーからステータスバーまで）。</summary>
public sealed partial class MainView : UserControl
{
    public MainView(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();

        LocalPane.ViewModel = viewModel.Local;
        RemotePane.ViewModel = viewModel.Remote;
        TransferPanel.ViewModel = viewModel.Transfers;
        TransferPanel.Log = viewModel.LogView;
        TransferPanel.Backups = viewModel.BackupList;
        TransferPanel.FilesDropped += OnTransferPanelFilesDropped;
        TransferSplitter.Dragged += OnTransferSplitterDragged;
        ContentArea.SizeChanged += (_, _) => ClampTransferPanel();
    }

    // ファイルの一覧に残す高さ（見出しと数行）
    private const double MinPaneHeight = 220;

    // 境目を上へ動かすと、転送の一覧が高くなる
    private void OnTransferSplitterDragged(object? sender, double deltaY)
    {
        ViewModel.Transfers.PanelHeight -= deltaY;
        ClampTransferPanel();
    }

    // ウインドウを小さくしても、ファイルの一覧が隠れないようにする
    private void ClampTransferPanel()
    {
        var available = ContentArea.ActualHeight - ContentArea.Padding.Top - ContentArea.Padding.Bottom - ContentArea.RowSpacing - MinPaneHeight;
        if (available <= 0)
            return;
        var max = Math.Max(Hakobune.Presentation.Transfers.TransferListViewModel.MinPanelHeight, available);
        if (ViewModel.Transfers.PanelHeight > max)
            ViewModel.Transfers.PanelHeight = max;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>つかんでウインドウを動かせる部分。</summary>
    public UIElement TitleBarElement => AppTitleBar;

    /// <summary>Mica の背景を見せる（無地の背景を外す）。</summary>
    public void UseTransparentBackground() => Root.Background = null;

    private async void OnQuickConnectInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        // ダイアログやメニューを開いているあいだは、重ねて出さない
        if (XamlRoot is null || ViewModel.QuickConnectCommand.IsRunning
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(popup => popup.Child is not ToolTip))
            return;
        await ViewModel.QuickConnectCommand.ExecuteAsync(null);
    }

    private async void OnUploadInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.UploadCommand.CanExecute(null))
            await ViewModel.UploadCommand.ExecuteAsync(null);
    }

    private async void OnDownloadInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.DownloadCommand.CanExecute(null))
            await ViewModel.DownloadCommand.ExecuteAsync(null);
    }

    // 転送の一覧にファイルを落としたら、サーバーのいまのフォルダへ送る
    private async void OnTransferPanelFilesDropped(object? sender, IReadOnlyList<string> paths) =>
        await ViewModel.UploadAsync(paths);

    // 接続先をクリックすると接続する（接続中のものなら何もしない）
    private async void OnConnectionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel connection)
            await ViewModel.ConnectAsync(connection);
    }

    // 右クリックのメニューの項目は、Tag（なければ DataContext）に接続先が入っている
    private static ConnectionItemViewModel? MenuTarget(object sender) =>
        sender is FrameworkElement element ? (element.Tag ?? element.DataContext) as ConnectionItemViewModel : null;

    private async void OnConnectMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } connection)
            await ViewModel.ConnectAsync(connection);
    }

    private async void OnDisconnectMenuClick(object sender, RoutedEventArgs e) => await ViewModel.DisconnectAsync();

    private async void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } connection)
            await ViewModel.EditConnectionCommand.ExecuteAsync(connection);
    }

    private async void OnDuplicateMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } connection)
            await ViewModel.DuplicateConnectionCommand.ExecuteAsync(connection);
    }

    private async void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } connection)
            await ViewModel.DeleteConnectionCommand.ExecuteAsync(connection);
    }
}
