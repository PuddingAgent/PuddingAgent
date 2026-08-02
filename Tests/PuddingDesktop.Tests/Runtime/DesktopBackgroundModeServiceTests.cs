using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Runtime;

public sealed class DesktopBackgroundModeServiceTests
{
    [Fact]
    public void DefaultClose_HidesToTrayUntilExplicitExit()
    {
        var service = new DesktopBackgroundModeService();

        Assert.True(service.ShouldMinimizeToTray());
        service.RequestExplicitExit();
        Assert.False(service.ShouldMinimizeToTray());
    }

    [Fact]
    public void ExitBehavior_DoesNotHideToTray()
    {
        var service = new DesktopBackgroundModeService();
        service.Configure(DesktopCloseBehavior.ExitAndStopCore);

        Assert.False(service.ShouldMinimizeToTray());
    }
}
