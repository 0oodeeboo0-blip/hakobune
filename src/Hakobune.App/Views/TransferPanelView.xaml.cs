using System.Diagnostics;
using System.Text;
using Hakobune.Presentation.Backups;
using Hakobune.Presentation.Logging;
using Hakobune.Presentation.Transfers;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Hakobune.App.Views;

/// <summary>下の転送の一覧。</summary>
public sealed partial class TransferPanelView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TransferListViewModel),
        typeof(TransferPanelView),
        new PropertyMetadata(null, OnViewModelChanged));

    public static readonly DependencyProperty LogProperty = DependencyProperty.Register(
        nameof(Log),
        typeof(LogViewModel),
        typeof(TransferPanelView),
        new PropertyMetadata(null, OnLogChanged));

    public static readonly DependencyProperty BackupsProperty = DependencyProperty.Register(
        nameof(Backups),
        typeof(BackupListViewModel),
        typeof(TransferPanelView),
        new PropertyMetadata(null, OnBackupsChanged));

    private ScrollViewer? _logScroller;

    public TransferPanelView()
    {
        InitializeComponent();
    }

    public TransferListViewModel? ViewModel
    {
        get => (TransferListViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>下の「ログ」。</summary>
    public LogViewModel? Log
    {
        get => (LogViewModel?)GetValue(LogProperty);
        set => SetValue(LogProperty, value);
    }

    /// <summary>下の「控え」。</summary>
    public BackupListViewModel? Backups
    {
        get => (BackupListViewModel?)GetValue(BackupsProperty);
        set => SetValue(BackupsProperty, value);
    }

    private static void OnBackupsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TransferPanelView)d).Bindings.Update();

    private void OnBackupsTabClick(object sender, RoutedEventArgs e) => ViewModel?.SelectBackups();

    // 控えの置き場をエクスプローラーで開く（まだ無ければ作る）
    private void OnOpenBackupFolderClick(object sender, RoutedEventArgs e)
    {
        if (Backups?.FolderPath is not { } folder)
            return;
        try
        {
            Directory.CreateDirectory(folder);
            using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TransferPanelView)d;
        if (e.OldValue is TransferListViewModel oldModel)
            oldModel.PropertyChanged -= view.OnViewModelPropertyChanged;
        if (e.NewValue is TransferListViewModel newModel)
            newModel.PropertyChanged += view.OnViewModelPropertyChanged;
        view.Bindings.Update();
    }

    // 「ログ」を開いたら（メニューや起動の指定からでも）、いちばん新しい行を見せる
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferListViewModel.IsLogSelected) && ViewModel is { IsLogSelected: true })
            ScrollLogToEnd();
    }

    private static void OnLogChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TransferPanelView)d;
        if (e.OldValue is LogViewModel oldLog)
            oldLog.RowsAppended -= view.OnLogRowsAppended;
        if (e.NewValue is LogViewModel newLog)
            newLog.RowsAppended += view.OnLogRowsAppended;
        view.Bindings.Update();
    }

    private void OnLogTabClick(object sender, RoutedEventArgs e) => ViewModel?.SelectLog();

    // 新しい行が増えた。いちばん下を見ていたら、ついていく（上を見ているあいだは動かさない）
    private void OnLogRowsAppended(object? sender, EventArgs e)
    {
        _logScroller ??= FindDescendant<ScrollViewer>(LogList);
        if (_logScroller is { } scroller && scroller.VerticalOffset < scroller.ScrollableHeight - 8)
            return;
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (Log is { Rows.Count: > 0 } log)
                LogList.ScrollIntoView(log.Rows[^1]);
        });

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
                return found;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    // 選んだ行があればそれを、なければ出している行を全部
    private string LogText()
    {
        if (Log is not { } log)
            return "";
        if (LogList.SelectedItems.Count == 0)
            return log.ToText();
        var text = new StringBuilder();
        foreach (var row in LogList.SelectedItems.OfType<LogRowViewModel>().OrderBy(row => row.Entry.Sequence))
            text.AppendLine(row.ToText());
        return text.ToString();
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e) => CopyLog();

    private void OnCopyLogInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CopyLog();
    }

    private void CopyLog()
    {
        var text = LogText();
        if (text.Length == 0)
            return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async void OnSaveLogClick(object sender, RoutedEventArgs e)
    {
        if (Log is not { } log || XamlRoot is null)
            return;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"hakobune-log-{DateTime.Now:yyyyMMdd-HHmm}",
            };
            picker.FileTypeChoices.Add("テキスト", [".txt"]);
            var window = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            // メモ帳などで文字化けしないよう、BOM 付きの UTF-8 にする
            await File.WriteAllTextAsync(file.Path, log.ToText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            CrashReport.Write(ex);
        }
    }

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
