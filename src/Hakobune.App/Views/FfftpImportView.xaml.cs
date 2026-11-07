using Hakobune.Presentation.Connections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Hakobune.App.Views;

/// <summary>FFFTP から接続先を読み込むダイアログの中身（<see cref="ContentDialog"/> に入れて出す）。</summary>
public sealed partial class FfftpImportView : UserControl
{
    public FfftpImportView(FfftpImportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
    }

    public FfftpImportViewModel ViewModel { get; }

    private void OnMasterPasswordChanged(object sender, RoutedEventArgs e) => ViewModel.MasterPassword = MasterPasswordInput.Password;

    // Enter で確かめる（ダイアログの「読み込む」は押さない）
    private void OnMasterPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        ViewModel.CheckMasterPasswordCommand.Execute(null);
    }
}
