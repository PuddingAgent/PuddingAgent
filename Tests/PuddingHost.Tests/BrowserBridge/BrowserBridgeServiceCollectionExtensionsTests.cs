using Microsoft.Extensions.DependencyInjection;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;
using PuddingHost.BrowserBridge;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class BrowserBridgeServiceCollectionExtensionsTests
{
    [Fact]
    public void DesktopChild_RegistersRemoteRuntimeAndSevenBrowserTools()
    {
        var services = new ServiceCollection();
        services.AddDesktopBrowserAutomation(Options(PuddingHostMode.DesktopChild, enabled: true));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBrowserRuntime));
        var toolTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPuddingTool))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null)
            .Select(type => type!.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["BrowserContextTool", "BrowserInteractTool", "BrowserLocateTool", "BrowserNavigateTool",
                "BrowserSnapshotTool", "BrowserTabsTool", "BrowserWaitForTool"],
            toolTypes);
    }

    [Theory]
    [InlineData(PuddingHostMode.Console, false)]
    [InlineData(PuddingHostMode.Console, true)]
    [InlineData(PuddingHostMode.DesktopChild, false)]
    public void NonDesktopOrDisabled_DoesNotExposeBrowserTools(
        PuddingHostMode mode,
        bool enabled)
    {
        var services = new ServiceCollection();
        services.AddDesktopBrowserAutomation(Options(mode, enabled));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IBrowserRuntime));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IPuddingTool));
    }

    private static PuddingHostOptions Options(PuddingHostMode mode, bool enabled) => new()
    {
        Mode = mode,
        DataRoot = Path.GetTempPath(),
        BrowserAutomationEnabled = enabled
    };
}
