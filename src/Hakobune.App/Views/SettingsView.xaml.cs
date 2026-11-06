using System.Diagnostics;
using Hakobune.Presentation.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Hakobune.App.Views;

/// <summary>設定の中身（<see cref="ContentDialog"/> に入れて出す）。</summary>
public sealed partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();

        // 選択肢は x:Bind の SelectedIndex より先に入れておく
        ThemeBox.ItemsSource = viewModel.ThemeNames;
        if (viewModel.OpensAtExclusions)
            Loaded += OnLoadedScrollToExclusions;
    }

    public SettingsViewModel ViewModel { get; }

    // 「送らないファイル」のところまで送る。ダイアログが開き終わるまでは、送れる長さが決まらないので、少し待ってから動かす
    private async void OnLoadedScrollToExclusions(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedScrollToExclusions;
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                if (FindScrollViewer() is not { ScrollableHeight: > 0, Content: UIElement content } scroller)
                    continue;
                var top = ExclusionsHeading.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
                scroller.ChangeView(null, Math.Min(top, scroller.ScrollableHeight), null, disableAnimation: true);
                return;
            }
        }
        catch (ArgumentException)
        {
            // 閉じたあとなど、画面の木から外れていたら何もしない
        }
    }

    private ScrollViewer? FindScrollViewer()
    {
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scroller)
                return scroller;
        }
        return null;
    }

    // 名前の欄で Enter を押すと足す（ダイアログが閉じないよう、ここで受け止める）
    private void OnPatternKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (sender is TextBox box)
            ViewModel.NewPattern = box.Text;
        ViewModel.AddPatternCommand.Execute(null);
    }

    private void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string pattern })
            ViewModel.AddSuggestedPatternCommand.Execute(pattern);
    }

    // 保存の場所をエクスプローラーで開く（まだ何も保存していなければ、フォルダが無いので何もしない）
    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(ViewModel.DataDirectory))
            return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(ViewModel.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
