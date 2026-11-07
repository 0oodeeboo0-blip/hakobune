using System.ComponentModel;
using System.Runtime.InteropServices;
using Hakobune.App.Helpers;
using Hakobune.Presentation.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using VisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;

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

        // マウスで囲んで選ぶ。行が先に受け取った（処理した）押す・動かす・離すも見たいので、処理済みのものも受け取る
        FileList.AddHandler(PointerPressedEvent, new PointerEventHandler(OnListPointerPressed), handledEventsToo: true);
        FileList.AddHandler(PointerMovedEvent, new PointerEventHandler(OnListPointerMoved), handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnListPointerReleased), handledEventsToo: true);
        FileList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnListPointerCaptureLost), handledEventsToo: true);
        FileList.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnListPointerCanceled), handledEventsToo: true);
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 囲んで選んでいる途中は、まとめて変えてから 1 回だけ知らせる
        if (!_applyingMarquee)
            ViewModel?.UpdateSelection(FileList.SelectedItems.OfType<FileRowViewModel>().ToList());
    }

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
        // 名前やアイコンの外から引いたときは、ファイルのドラッグではなく、囲んで選ぶ
        if (ViewModel is not { } viewModel || _marquee is not null)
        {
            e.Cancel = true;
            return;
        }
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

    // ---- マウスで囲んで選ぶ ----
    // エクスプローラーと同じく、名前やアイコンを掴んだらファイルのドラッグ、それ以外の所（行の空いている所、一覧の下の空白）から
    // 引いたら枠を出し、枠にかかった行を選ぶ。Ctrl を押しながらなら、選んでいたものに足す。上下の端の外まで引くと、一覧が流れる。

    private const string RowHandleTag = "RowHandle";

    // これだけ動いたら、囲み始める（ファイルのドラッグが始まる距離より短くする）
    private const double MarqueeThreshold = 3;

    private MarqueeState? _marquee;
    private bool _applyingMarquee;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _marqueeTimer;
    private double _autoScrollStep;
    private ScrollViewer? _scroller;

    private enum PressZone
    {
        // 名前やアイコン（ファイルのドラッグ）、スクロールバー
        Ignore,

        // 行の空いている所
        Row,

        // 行の無い所
        Empty,
    }

    private void OnListPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        EndMarquee();
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || FileList.ItemsPanelRoot is not { } panel)
            return;
        var point = e.GetCurrentPoint(FileList);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        var zone = Classify(e.OriginalSource as DependencyObject);
        if (zone == PressZone.Ignore)
            return;
        var additive = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        IReadOnlyList<ItemIndexRange> initial = additive
            ? [.. FileList.SelectedRanges.Select(range => new ItemIndexRange(range.FirstIndex, range.Length))]
            : [];
        _marquee = new MarqueeState
        {
            PointerId = e.Pointer.PointerId,
            PressedSource = e.OriginalSource as DependencyObject,
            Start = e.GetCurrentPoint(panel).Position,
            StartInList = point.Position,
            OnEmptySpace = zone == PressZone.Empty,
            Additive = additive,
            Initial = initial,
            Current = point.Position,
        };
    }

    private void OnListPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is not { } marquee || e.Pointer.PointerId != marquee.PointerId)
            return;
        var point = e.GetCurrentPoint(FileList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            // 離したのを受け取りそこねた（ウインドウの外で離したなど）
            var wasActive = marquee.IsActive;
            EndMarquee();
            if (wasActive)
                FileList.ReleasePointerCapture(e.Pointer);
            return;
        }
        marquee.Current = point.Position;
        if (!marquee.IsActive)
        {
            if (Math.Abs(point.Position.X - marquee.StartInList.X) < MarqueeThreshold
                && Math.Abs(point.Position.Y - marquee.StartInList.Y) < MarqueeThreshold)
                return;
            // 囲み始める。マウスをこちらで受け持ち、行がファイルのドラッグを始めないようにする
            marquee.IsActive = true;
            TakeCapture(marquee.PressedSource, e.Pointer);
            FocusList(marquee);
            MarqueeBox.Visibility = Visibility.Visible;
            StartMarqueeTimer();
        }
        e.Handled = true;
        UpdateMarquee();
        UpdateAutoScroll();
    }

    private void OnListPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is not { } marquee || e.Pointer.PointerId != marquee.PointerId)
            return;
        if (marquee.IsActive)
        {
            e.Handled = true;
            marquee.Current = e.GetCurrentPoint(FileList).Position;
            UpdateMarquee();
            EndMarquee();
            FileList.ReleasePointerCapture(e.Pointer);
            return;
        }
        // 行の無い所を押しただけなら、選んでいたものを外す（エクスプローラーと同じ）
        if (marquee.OnEmptySpace && !marquee.Additive && FileList.SelectedItems.Count > 0)
            FileList.SelectedItems.Clear();
        EndMarquee();
    }

    private void OnListPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is { } marquee && e.Pointer.PointerId == marquee.PointerId)
            EndMarquee();
    }

    // 押したときに行がマウスを受け持つので、手放させてから一覧で受け持つ。行に受け持たせたままだと、
    // 流して行が見えなくなったところで受け持ちが外れ、離したのが一覧に届かなくなる
    private void TakeCapture(DependencyObject? pressed, Pointer pointer)
    {
        for (var current = pressed; current is not null && !ReferenceEquals(current, FileList); current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.PointerCaptures?.Any(captured => captured.PointerId == pointer.PointerId) == true)
                element.ReleasePointerCapture(pointer);
        }
        FileList.CapturePointer(pointer);
    }

    // 一覧がマウスを受け持てなくなった（別のウインドウに切り替えたなど）。行がマウスを手放したときは、ここに来ても続ける
    private void OnListPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is { IsActive: true } && ReferenceEquals(e.OriginalSource, FileList))
            EndMarquee();
    }

    private PressZone Classify(DependencyObject? source)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, FileList); current = VisualTreeHelper.GetParent(current))
        {
            switch (current)
            {
                case FrameworkElement { Tag: RowHandleTag }:
                case ScrollBar:
                case Thumb:
                    return PressZone.Ignore;
                case ListViewItem:
                    return PressZone.Row;
            }
        }
        return PressZone.Empty;
    }

    // 囲み始めたら、キーボードの操作（Delete など）がこちらの一覧に届くようにする。
    // 一覧そのものに渡すと、前に選んでいた行まで流れることがあるので、押した所の行に渡す
    private void FocusList(MarqueeState marquee)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInsideList(focused))
            return;
        var count = FileList.Items.Count;
        if (count > 0 && FileList.ItemsPanelRoot is { } panel)
        {
            var (offset, pitch) = RowLayout(panel);
            var index = Math.Clamp((int)Math.Floor((marquee.Start.Y - offset) / pitch), 0, count - 1);
            if (FileList.ContainerFromIndex(index) is ListViewItem item && item.Focus(FocusState.Pointer))
                return;
        }
        FileList.Focus(FocusState.Pointer);
    }

    private bool IsInsideList(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, FileList))
                return true;
        }
        return false;
    }

    // 枠を描き直し、枠にかかった行を選ぶ
    private void UpdateMarquee()
    {
        if (_marquee is not { IsActive: true } marquee || FileList.ItemsPanelRoot is not { } panel)
            return;
        try
        {
            // 行を並べた板の上の位置で比べる（一覧が流れても、始めた位置は行と一緒に動く）
            var current = FileList.TransformToVisual(panel).TransformPoint(marquee.Current);
            var top = Math.Min(marquee.Start.Y, current.Y);
            var bottom = Math.Max(marquee.Start.Y, current.Y);
            DrawMarquee(panel, marquee.Start, current);

            // 行はどれも同じ高さなので、上下の位置から番号が分かる（見えていない行も選べる）
            (int First, int Last)? range = null;
            var count = FileList.Items.Count;
            var (offset, pitch) = RowLayout(panel);
            if (count > 0 && bottom >= offset && top < offset + count * pitch)
            {
                var first = Math.Clamp((int)Math.Floor((top - offset) / pitch), 0, count - 1);
                var last = Math.Clamp((int)Math.Floor((bottom - offset) / pitch), 0, count - 1);
                range = (first, last);
            }
            if (marquee.HasApplied && range == marquee.Range)
                return;
            marquee.Range = range;
            marquee.HasApplied = true;
            ApplyMarqueeSelection(marquee);
        }
        catch (ArgumentException)
        {
            // 一覧が作り直されている途中など。次に動かしたときにまた試す
        }
    }

    // 枠は、一覧の見えている所に収めて描く
    private void DrawMarquee(Panel panel, Point start, Point current)
    {
        var toLayer = panel.TransformToVisual(MarqueeLayer);
        var a = toLayer.TransformPoint(start);
        var b = toLayer.TransformPoint(current);
        var width = MarqueeLayer.ActualWidth;
        var height = MarqueeLayer.ActualHeight;
        var left = Math.Clamp(Math.Min(a.X, b.X), 0, width);
        var right = Math.Clamp(Math.Max(a.X, b.X), 0, width);
        var top = Math.Clamp(Math.Min(a.Y, b.Y), 0, height);
        var bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, height);
        Canvas.SetLeft(MarqueeBox, left);
        Canvas.SetTop(MarqueeBox, top);
        MarqueeBox.Width = right - left;
        MarqueeBox.Height = bottom - top;
    }

    private void ApplyMarqueeSelection(MarqueeState marquee)
    {
        var count = FileList.Items.Count;
        if (count == 0)
            return;
        _applyingMarquee = true;
        try
        {
            FileList.DeselectRange(new ItemIndexRange(0, (uint)count));
            // 押す前に選んでいたもの（Ctrl を押していたとき）。一覧が変わっていたら、はみ出す分は捨てる
            foreach (var range in marquee.Initial)
            {
                if (range.FirstIndex < count)
                    FileList.SelectRange(new ItemIndexRange(range.FirstIndex, (uint)Math.Min(range.Length, count - range.FirstIndex)));
            }
            if (marquee.Range is { } selected)
                FileList.SelectRange(new ItemIndexRange(selected.First, (uint)(selected.Last - selected.First + 1)));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
        }
        finally
        {
            _applyingMarquee = false;
        }
        ViewModel?.UpdateSelection(FileList.SelectedItems.OfType<FileRowViewModel>().ToList());
    }

    // 行の位置: 0 番目の行の上端と、行の間隔。並んでいる行から測る（測れなければ、決まった高さ）
    private (double Offset, double Pitch) RowLayout(Panel panel)
    {
        if (panel is ItemsStackPanel { FirstVisibleIndex: >= 0 } stack)
        {
            var index = stack.FirstVisibleIndex;
            if (FileList.ContainerFromIndex(index) is FrameworkElement first)
            {
                var top = first.TransformToVisual(panel).TransformPoint(default).Y;
                if (FileList.ContainerFromIndex(index + 1) is FrameworkElement second)
                {
                    var pitch = second.TransformToVisual(panel).TransformPoint(default).Y - top;
                    if (pitch > 1)
                        return (top - index * pitch, pitch);
                }
                if (first.ActualHeight > 1)
                {
                    var pitch = first.ActualHeight + first.Margin.Top + first.Margin.Bottom;
                    return (top - first.Margin.Top - index * pitch, pitch);
                }
            }
        }
        return (0, ViewModel?.UseCompactRows == true ? 28 : 34);
    }

    // 上下の端の外まで引いているあいだは、離れているほど速く一覧を流す（流すのは、囲んでいるあいだ動いている時計が行う）
    private void UpdateAutoScroll()
    {
        if (_marquee is not { IsActive: true } marquee)
        {
            _autoScrollStep = 0;
            return;
        }
        const double Edge = 4;
        var y = marquee.Current.Y;
        var height = FileList.ActualHeight;
        var overshoot = y < Edge ? y - Edge : y > height - Edge ? y - (height - Edge) : 0;
        _autoScrollStep = overshoot == 0 ? 0 : Math.Sign(overshoot) * Math.Min(60, 4 + Math.Abs(overshoot) / 2);
    }

    private void StartMarqueeTimer()
    {
        if (_marqueeTimer is null)
        {
            _marqueeTimer = DispatcherQueue.CreateTimer();
            _marqueeTimer.Interval = TimeSpan.FromMilliseconds(30);
            _marqueeTimer.Tick += OnMarqueeTick;
        }
        if (!_marqueeTimer.IsRunning)
            _marqueeTimer.Start();
    }

    private void OnMarqueeTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_marquee is not { IsActive: true })
        {
            sender.Stop();
            return;
        }
        // 離したのが届かなかったとき（どこかで受け持ちが外れたなど）も、ボタンを見て終える。枠が残らないように
        if (!IsPrimaryButtonDown())
        {
            UpdateMarquee();
            EndMarquee();
            FileList.ReleasePointerCaptures();
            return;
        }
        if (_autoScrollStep == 0)
            return;
        _scroller ??= FindScrollViewer(FileList);
        if (_scroller is not { } scroller)
            return;
        // 前の回に流した分を、枠と選択に入れてから、また流す
        UpdateMarquee();
        var target = Math.Clamp(scroller.VerticalOffset + _autoScrollStep, 0, scroller.ScrollableHeight);
        if (Math.Abs(target - scroller.VerticalOffset) >= 0.5)
            scroller.ChangeView(null, target, null, disableAnimation: true);
    }

    // 主ボタン（ふつうは左。左利きの設定なら右）を押しているか
    private static bool IsPrimaryButtonDown()
    {
        const int SwapButton = 23;
        const int LeftButton = 0x01;
        const int RightButton = 0x02;
        var button = GetSystemMetrics(SwapButton) != 0 ? RightButton : LeftButton;
        return (GetAsyncKeyState(button) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private void EndMarquee()
    {
        _marquee = null;
        _autoScrollStep = 0;
        MarqueeBox.Visibility = Visibility.Collapsed;
        _marqueeTimer?.Stop();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
                return scroller;
            if (FindScrollViewer(child) is { } found)
                return found;
        }
        return null;
    }

    private sealed class MarqueeState
    {
        public required uint PointerId { get; init; }

        // 押した所（行がマウスを受け持っていれば、そこから手放させる）
        public DependencyObject? PressedSource { get; init; }

        // 押した位置（行を並べた板の上）
        public required Point Start { get; init; }

        // 押した位置（一覧の見えている所の上。動き出しの判定に使う）
        public required Point StartInList { get; init; }

        public required bool OnEmptySpace { get; init; }

        // Ctrl を押していたか（押していれば、もとの選択に足す）
        public required bool Additive { get; init; }

        public required IReadOnlyList<ItemIndexRange> Initial { get; init; }

        // いまのマウスの位置（一覧の見えている所の上）
        public required Point Current { get; set; }

        public bool IsActive { get; set; }

        public bool HasApplied { get; set; }

        public (int First, int Last)? Range { get; set; }
    }

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
