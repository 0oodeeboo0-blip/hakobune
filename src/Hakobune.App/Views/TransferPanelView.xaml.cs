using Hakobune.Presentation.Transfers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

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

    /// <summary>エクスプローラーからファイルが落とされた（サーバーへ送る）。</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "サーバーへアップロード";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
            if (paths.Count > 0)
                FilesDropped?.Invoke(this, paths);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            CrashReport.Write(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

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
