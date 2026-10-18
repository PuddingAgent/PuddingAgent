using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Xunit;

namespace DesktopServiceTests;

/// <summary>
/// 浏览器执行器组合根（ADR-097 D2）：执行器在浏览器工作区生命周期内构造，与能力通道开关无关；
/// 共享权威的关闭权归属必须可判定（通道停用 ≠ 卸载浏览器）。
///
/// 本批只钉住**装配与所有权**；"Bridge 截图经执行器成功返回"需要会话准入（I2）才能成立，
/// 不在本批伪造证据。
/// </summary>
public sealed class DesktopBrowserExecutorCompositionTests
{
    private static readonly DesktopPageTarget AgentPage = new("ctx-agent", "page-1");

    [Fact]
    public void CreateService_ReusesTheLifecycleRegistriesAndAuthority()
    {
        var lifecycle = TestAuthorities.CreateLifecycle();

        var service = DesktopBrowserExecutorComposition.CreateService(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            lifecycle,
            options: BrowserOptions());

        Assert.Same(lifecycle.PageTargets, service.Targets);
        Assert.Same(lifecycle.Authority, service.Authority);
        service.Close();
    }

    [Fact]
    public async Task Executor_ConstructedWithoutAnyCapabilityChannel_IsUsable()
    {
        var lifecycle = ReadyLifecycle();

        // 默认配置（能力通道关闭）也必须能构造出本地执行器：这是 F1“默认 Bridge 截图无执行器”的修复前提。
        await using var executor = DesktopBrowserExecutorComposition.Create(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            lifecycle,
            options: BrowserOptions());

        Assert.False(executor.IsClosed);
        Assert.False(executor.Interaction.IsUserTakeover);
    }

    [Fact]
    public async Task Executor_WithoutAuthorityOwnership_DoesNotCloseTheSharedAuthority()
    {
        var lifecycle = ReadyLifecycle();

        var channelSide = DesktopBrowserExecutorComposition.Create(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            lifecycle,
            options: BrowserOptions(),
            closesAuthorityOnDispose: false);

        await channelSide.DisposeAsync();

        // 通道停用/重启会调用这条释放路径；共享权威必须仍然活着，否则 Bridge 路径被永久关死。
        Assert.False(lifecycle.Authority.Capture().IsClosed);
    }

    [Fact]
    public async Task Executor_WithAuthorityOwnership_ClosesTheSharedAuthority()
    {
        var lifecycle = ReadyLifecycle();

        var executor = DesktopBrowserExecutorComposition.Create(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            lifecycle,
            options: BrowserOptions());

        await executor.DisposeAsync();

        Assert.True(lifecycle.Authority.Capture().IsClosed);
    }

    private static BrowserPageLifecycle ReadyLifecycle()
    {
        var lifecycle = TestAuthorities.CreateLifecycle();
        lifecycle.NotifyBridgeConnected(7);
        lifecycle.RegisterContext(AgentPage.ContextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.RegisterPage(
            AgentPage,
            DesktopPageVersion.Require(3),
            DesktopPageReadiness.Interactive);
        lifecycle.SetAgentTarget(AgentPage.ContextId, AgentPage.PageId);
        return lifecycle;
    }

    private static DesktopServiceOptions BrowserOptions() => new()
    {
        AllowedCapabilities = DesktopCapability.BrowserScreenshot,
        BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
    };
}
