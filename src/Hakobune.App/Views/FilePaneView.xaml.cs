using System.ComponentModel;
using Hakobune.App.Helpers;
using Hakobune.Presentation.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Hakobune.App.Views;

/// <summary>パソコン側またはサーバー側の一覧。</summary>
public sealed partial class FilePaneView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(FilePaneViewModel),
        typeof(FilePaneView),
        new PropertyMetadata(null, OnViewModelChanged));

    public FilePaneView()
    {
        InitializeComponent();
        // ショートカットは、こちら側にフォーカスがあるときだけ効かせる（左右で取り合わない）
        foreach (var accelerator in KeyboardAccelerators)
            accelerator.ScopeOwner = this;
    }

    public FilePaneViewModel? ViewModel
    {
        get => (FilePaneViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (FilePaneView)d;
        if (e.OldValue is FilePaneViewModel oldViewModel)
            oldViewModel.PropertyChanged -= view.OnViewModelPropertyChanged;
        if (e.NewValue is FilePaneViewModel newViewModel)
            newViewModel.PropertyChanged += view.OnViewModelPropertyChanged;
        view.Bindings.Update();
        view.ApplyFocusItem();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePaneViewModel.FocusItem))
            ApplyFocusItem();
    }

    // 上のフォルダに戻ったときなどに、元のフォルダを選んで見える位置に出す
    private void ApplyFocusItem()
    {
        if (ViewModel?.FocusItem is not { } item)
            return;
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel?.UpdateSelection(FileList.SelectedItems.OfType<FileRowViewModel>().ToList());

    // 取り直しで増えた・変わった行を、そっと光らせる（行の入れ物が別の行に使われるたびに呼ばれる）
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not Panel root || root.Children.Count == 0 || root.Children[0] is not Border glow)
            return;
        RowGlow.Show(glow, !args.InRecycleQueue && args.Item is FileRowViewModel { IsHighlighted: true });
    }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && (e.OriginalSource as FrameworkElement)?.DataContext is FileRowViewModel row)
            await viewModel.OpenAsync(row);
    }

    private async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        switch (e.Key)
        {
            case VirtualKey.Enter when FileList.SelectedItem is FileRowViewModel row:
                e.Handled = true;
                await viewModel.OpenAsync(row);
                break;
            case VirtualKey.Back:
                e.Handled = true;
                await viewModel.GoUpCommand.ExecuteAsync(null);
                break;
            case VirtualKey.Delete:
                e.Handled = true;
                viewModel.RequestAction(PaneAction.Delete);
                break;
            case VirtualKey.F2:
                e.Handled = true;
                viewModel.RequestAction(PaneAction.Rename);
                break;
        }
    }

    private void OnNewFolderInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.RequestAction(PaneAction.NewFolder);
    }

    private void OnRenameInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.RequestAction(PaneAction.Rename);
    }

    private void OnPermissionsInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.RequestAction(PaneAction.ChangePermissions);
    }

    // ---- ドラッグ＆ドロップ ----

    // 同じアプリの中で、どちらの一覧のどの行をドラッグしているか
    private static (FilePaneViewModel Pane, IReadOnlyList<FileRowViewModel> Rows)? s_dragging;

    private const string DragFormat = "Hakobune.Rows";

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        var rows = e.Items.OfType<FileRowViewModel>().Where(row => !row.IsDrive).ToList();
        if (rows.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        s_dragging = (viewModel, rows);
        e.Data.SetData(DragFormat, viewModel.Kind.ToString());
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (e.DataView.Contains(DragFormat) && s_dragging is { } dragging && !ReferenceEquals(dragging.Pane, viewModel))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = viewModel.Kind == PaneKind.Remote ? "サーバーへアップロード" : "パソコンへダウンロード";
        }
        else if (viewModel.Kind == PaneKind.Remote && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "サーバーへアップロード";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (e.DataView.Contains(DragFormat) && s_dragging is { } dragging)
        {
            s_dragging = null;
            viewModel.DropRows(dragging.Pane, dragging.Rows);
            return;
        }
        if (viewModel.Kind != PaneKind.Remote || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            viewModel.DropFiles([.. items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path))]);
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

    private void OnGotFocus(object sender, RoutedEventArgs e) => ViewModel?.Activate();

    private void OnFindInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    }

    private async void OnGoUpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel is { } viewModel)
            await viewModel.GoUpCommand.ExecuteAsync(null);
    }

    private async void OnRefreshInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel is { } viewModel)
            await viewModel.RefreshAsync();
    }

    // 絞り込みの欄: Esc で消して一覧へ、Enter で一覧へ
    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (ViewModel is { } viewModel)
                    viewModel.FilterText = "";
                FileList.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (FileList.Items.Count > 0 && FileList.SelectedIndex < 0)
                    FileList.SelectedIndex = 0;
                FileList.Focus(FocusState.Keyboard);
                break;
        }
    }

    private async void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (ViewModel is { } viewModel && args.Item is PathSegment segment)
            await viewModel.NavigateAsync(segment.Path);
    }

    private void OnSortByName(object sender, RoutedEventArgs e) => ViewModel?.SortBy(FileSortColumn.Name);

    private void OnSortBySize(object sender, RoutedEventArgs e) => ViewModel?.SortBy(FileSortColumn.Size);

    private void OnSortByModified(object sender, RoutedEventArgs e) => ViewModel?.SortBy(FileSortColumn.Modified);
}
