using Microsoft.Extensions.DependencyInjection;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;
using PuddingHost.BrowserBridge;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class BrowserBridgeServiceCollectionExtensionsTests
{
    [Fact]
    public void DesktopChild_RegistersRemoteRuntimeAndAllBrowserTools()
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
                "BrowserScreenshotTool", "BrowserSnapshotTool", "BrowserTabsTool", "BrowserWaitForTool"],
            toolTypes);
    }

    [Fact]
    public void TheCallContextFactoryIsWiredToTheCallerIdentityScope()
    {
        // §3.1 的组合根约束：工厂必须**真的**拿到身份读取器。这条用例是在一次静默失效
        // （字符串替换没生效、编译照过、生产路径其实没接上）之后补的 —— 用行为/结构断言钉住。
        var services = new ServiceCollection();
        services.AddDesktopBrowserAutomation(Options(PuddingHostMode.DesktopChild, enabled: true));
        using var provider = services.BuildServiceProvider();

        var accessor = provider.GetRequiredService<IBrowserCallerIdentityAccessor>();
        Assert.IsType<BrowserCallerIdentityAccessor>(accessor);

        var factory = provider.GetRequiredService<IDesktopCapabilityCallContextFactory>();
        var field = factory.GetType().GetField(
            "_callerIdentity",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Same(accessor, field!.GetValue(factory));
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
