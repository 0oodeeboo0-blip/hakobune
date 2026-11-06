using Microsoft.UI.Xaml;

namespace Hakobune.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "Hakobune";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }
}
