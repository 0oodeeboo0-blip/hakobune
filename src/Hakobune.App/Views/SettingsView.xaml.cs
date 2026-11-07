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
        BackupKeepBox.ItemsSource = viewModel.BackupKeepNames;
        BackupSizeBox.ItemsSource = viewModel.BackupSizeNames;
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// 「送らないファイル」のところを見せる。ダイアログが開き終わってから呼ぶ。
    /// 開いた直後は、ダイアログが選択を最初の部品へ移して上まで戻すことがあるので、少しのあいだ何度か合わせ直す。
    /// </summary>
    public void ShowExclusions() => _ = ShowExclusionsAsync();

    private async Task ShowExclusionsAsync()
    {
        try
        {
            ExclusionsToggle.Focus(FocusState.Programmatic);
            foreach (var delay in new[] { 0, 100, 200, 300 })
            {
                if (delay > 0)
                    await Task.Delay(delay);
                ScrollToExclusions();
            }
        }
        catch (ArgumentException)
        {
            // 閉じたあとなど、画面の木から外れていたら何もしない
        }
    }

    private void ScrollToExclusions()
    {
        for (var current = VisualTreeHelper.GetParent(this); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer { Content: UIElement content } scroller)
            {
                var top = ExclusionsHeading.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
                scroller.ChangeView(null, top, null, disableAnimation: true);
                return;
            }
        }
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
