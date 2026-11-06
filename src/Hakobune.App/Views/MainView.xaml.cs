using Hakobune.Presentation;
using Hakobune.Presentation.Connections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    }

    public MainViewModel ViewModel { get; }

    /// <summary>つかんでウインドウを動かせる部分。</summary>
    public UIElement TitleBarElement => AppTitleBar;

    /// <summary>Mica の背景を見せる（無地の背景を外す）。</summary>
    public void UseTransparentBackground() => Root.Background = null;

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
