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

    private void OnConnectionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel connection)
            ViewModel.Connections.Select(connection);
    }
}
