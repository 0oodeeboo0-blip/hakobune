using Hakobune.Presentation.Demo;

namespace Hakobune.Presentation.Tests;

public class MainViewModelTests
{
    [Fact]
    public async Task The_toolbar_acts_on_the_pane_touched_last()
    {
        using var main = await DemoData.CreateAsync(DemoScenario.Normal);
        Assert.Same(main.Local, main.ActivePane);
        Assert.True(main.Local.IsActive);

        main.Remote.Activate();

        Assert.Same(main.Remote, main.ActivePane);
        Assert.True(main.Remote.IsActive);
        Assert.False(main.Local.IsActive);
    }

    [Fact]
    public async Task The_sidebar_can_be_closed_and_opened()
    {
        using var main = await DemoData.CreateAsync(DemoScenario.Normal);

        main.ToggleSidebarCommand.Execute(null);
        Assert.False(main.IsSidebarOpen);
        main.ToggleSidebarCommand.Execute(null);
        Assert.True(main.IsSidebarOpen);
    }
}
