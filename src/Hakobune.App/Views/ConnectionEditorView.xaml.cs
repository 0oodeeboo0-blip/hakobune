using System.ComponentModel;
using Hakobune.Presentation.Connections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Hakobune.App.Views;

/// <summary>接続先の作成・編集の中身（<see cref="ContentDialog"/> に入れて出す）。</summary>
public sealed partial class ConnectionEditorView : UserControl
{
    private readonly Func<nint> _windowHandle;

    /// <summary>
    /// 中身を作る。<paramref name="windowHandle"/> は、ファイルを選ぶ画面の持ち主にするウインドウ。
    /// </summary>
    public ConnectionEditorView(ConnectionEditorViewModel viewModel, Func<nint> windowHandle)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(windowHandle);
        ViewModel = viewModel;
        _windowHandle = windowHandle;
        InitializeComponent();

        // 選択肢は x:Bind の SelectedIndex より先に入れておく（x:Bind は読み込みのときに値を入れる）
        ProtocolBox.ItemsSource = ConnectionEditorViewModel.ProtocolChoices;
        EncodingBox.ItemsSource = ConnectionEditorViewModel.EncodingChoices;

        // パスワードの欄は、確実に両方向で同じにするため、ここでつなぐ
        PasswordInput.Password = viewModel.Password;
        PasswordInput.PasswordChanged += (_, _) => ViewModel.Password = PasswordInput.Password;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += (_, _) =>
        {
            if (ViewModel.IsNew)
                HostBox.Focus(FocusState.Programmatic);
        };
    }

    public ConnectionEditorViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // URL を貼り付けると、パスワードも埋まる
        if (e.PropertyName == nameof(ConnectionEditorViewModel.Password) && PasswordInput.Password != ViewModel.Password)
            PasswordInput.Password = ViewModel.Password;
    }

    private async void OnBrowsePrivateKey(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
            if (await picker.PickSingleFileAsync() is { } file)
                ViewModel.PrivateKeyPath = file.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            // ファイルを選ぶ画面が出せなくても、欄に直接書ける
            CrashReport.Write(ex);
        }
    }

    private async void OnBrowseLocalFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
            if (await picker.PickSingleFolderAsync() is { } folder)
                ViewModel.LocalDirectory = folder.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            CrashReport.Write(ex);
        }
    }
}
