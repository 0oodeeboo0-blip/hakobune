using Hakobune.Presentation.Transfers;
using Microsoft.UI.Xaml.Controls;

namespace Hakobune.App.Views;

/// <summary>同期の確認の中身（<see cref="ContentDialog"/> に入れて出す）。</summary>
public sealed partial class SyncView : UserControl
{
    public SyncView(SyncViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SyncViewModel ViewModel { get; }
}
