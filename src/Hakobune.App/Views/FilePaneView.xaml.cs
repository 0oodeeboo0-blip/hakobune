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
        ViewModel?.UpdateSelection(FileList.SelectedItems.Count);

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
            case VirtualKey.F5:
                e.Handled = true;
                await viewModel.RefreshAsync();
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
