using System.ComponentModel;
using Hakobune.Presentation.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
