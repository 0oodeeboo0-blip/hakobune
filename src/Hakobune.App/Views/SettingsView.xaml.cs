using System.Diagnostics;
using Hakobune.Presentation.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        {
            Loaded += (_, _) => ExclusionsHeading.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
            });
        }
    }

    public SettingsViewModel ViewModel { get; }

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
