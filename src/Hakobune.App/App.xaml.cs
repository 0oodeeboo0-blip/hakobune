using Hakobune.Presentation;
using Hakobune.Presentation.Demo;
using Microsoft.UI.Xaml;

namespace Hakobune.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        Options = AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        InitializeComponent();
        if (Options.Theme is { } theme)
            RequestedTheme = theme;
        UnhandledException += OnUnhandledException;
    }

    /// <summary>起動のときの指定。</summary>
    internal static AppOptions Options { get; private set; } = AppOptions.Default;

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            var viewModel = Options.Demo is { } scenario
                ? await DemoData.CreateAsync(scenario, SynchronizationContext.Current)
                : await MainViewModel.CreateAsync();
            _window = new MainWindow(viewModel, Options);
            _window.Activate();
        }
        catch (Exception ex)
        {
            CrashReport.Write(ex);
            Environment.Exit(1);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashReport.Write(e.Exception);
        if (Options.ScreenshotPath is not null)
            Environment.Exit(1);
    }
}
