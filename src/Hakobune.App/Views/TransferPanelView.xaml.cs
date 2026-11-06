using Hakobune.Presentation.Transfers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hakobune.App.Views;

/// <summary>下の転送の一覧。</summary>
public sealed partial class TransferPanelView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TransferListViewModel),
        typeof(TransferPanelView),
        new PropertyMetadata(null, OnViewModelChanged));

    public TransferPanelView()
    {
        InitializeComponent();
    }

    public TransferListViewModel? ViewModel
    {
        get => (TransferListViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TransferPanelView)d).Bindings.Update();

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferTabViewModel tab)
            ViewModel?.SelectTab(tab.Tab);
    }

    private static TransferRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as TransferRowViewModel;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            ViewModel?.Cancel(row);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            ViewModel?.Retry(row);
    }

    private void OnRetryNowClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            ViewModel?.RetryNow(row);
    }
}
