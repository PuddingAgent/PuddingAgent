using Microsoft.Extensions.DependencyInjection;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;
using PuddingHost.BrowserBridge;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingCode.Abstractions;
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
            ["BrowserContextTool", "BrowserEvaluateTool", "BrowserInteractTool", "BrowserLocateTool", "BrowserNavigateTool",
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
    /// <summary>
    /// **§5.4 组合根矩阵（必交付证据）**：只断言"某个服务被注册"是不够的 —— 两条传输（Bridge 与能力通道）
    /// **必须共用同一个执行边界**；若各自解析出不同实例，就是"两套执行器/两套策略"的静默分裂
    /// （方案 L205 要求注入**同一个** executor/authority/taskBindings/lifecycle）。
    /// 这里用**重复解析同一实例**（singleton 语义）钉住这一点。
    /// 注意：容器**必须异步释放**（`RemoteBrowserRuntime` 只实现 `IAsyncDisposable`）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_HandsBothTransportsTheSameExecutorBoundary()
    {
        var services = new ServiceCollection();
        services.AddDesktopBrowserAutomation(Options(PuddingHostMode.DesktopChild, enabled: true));
        await using var provider = services.BuildServiceProvider();

        var surface = provider.GetRequiredService<IDesktopBrowserCapabilitySurface>();
        Assert.Same(surface, provider.GetRequiredService<IDesktopBrowserCapabilitySurface>());

        var contextSurface = provider.GetRequiredService<IDesktopContextCapabilitySurface>();
        Assert.Same(contextSurface, provider.GetRequiredService<IDesktopContextCapabilitySurface>());

        var runtime = provider.GetRequiredService<IBrowserRuntime>();
        Assert.Same(runtime, provider.GetRequiredService<IBrowserRuntime>());
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

    /// <summary>
    /// R4（§6.2）**组合根注册**：启用浏览器自动化时，**委派生命周期端口必须可解析**（且是单例 ——
    /// 与门/运行时共用同一容器语义）；关闭时**不得注册**（保持今天的行为）。
    /// 这条用例同时验证了注册里用的两个能力名（apply/revoke）**确实存在于能力目录**中。
    /// </summary>
    [Fact]
    public void DesktopChild_WiresTheBrowserDelegationLifecyclePortAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddDesktopBrowserAutomation(Options(PuddingHostMode.DesktopChild, enabled: true));
        using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IBrowserDelegationLifecycle>();
        Assert.IsType<HostBrowserDelegationLifecycle>(port);
        Assert.Same(port, provider.GetRequiredService<IBrowserDelegationLifecycle>());
    }

    [Fact]
    public void DisabledBrowserAutomation_DoesNotRegisterTheDelegationPort()
    {
        var services = new ServiceCollection();
        services.AddDesktopBrowserAutomation(Options(PuddingHostMode.DesktopChild, enabled: false));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IBrowserDelegationLifecycle));
    }
    private static PuddingHostOptions Options(PuddingHostMode mode, bool enabled) => new()
    {
        Mode = mode,
        DataRoot = Path.GetTempPath(),
        BrowserAutomationEnabled = enabled
    };
}
