using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Xunit;

namespace DesktopServiceTests;

/// <summary>
/// 默认页级授权**接线**的断言（用户裁定 2026-10-25：桌面浏览器面板本来就是给 Agent 用的，
/// 不该要求先点「设为 Agent 目标」）。
///
/// 为什么需要这条用例：纯策略 <see cref="BrowserDefaultPageGrantPolicy"/> 已有单测，
/// 但「<c>DesktopService</c> 真的会调用它并签出授权」这一层此前**只被编译验证**——
/// 全仓 grep <c>desktop:agent-default</c> 在测试里 0 命中。
/// 若接线断掉，表现是写操作在授权到期前稳定被拒、读操作照常可用，很难从现象倒推。
///
/// 判据用「调用是否真的到达 UI 缝隙」（<see cref="RecordingUiSurface.ScreenshotCount"/>），
/// 而不是只看响应成败：替身 surface 对截图一律返回失败，这是既有用例约定的写法。
/// </summary>
public sealed class BrowserDefaultAgentGrantWiringTests
{
    private static readonly DesktopPageTarget AgentPage = new("ctx-agent", "page-1");
    private static readonly DesktopInstanceId DesktopId = new("desk-test");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-test");
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(1);

    [Fact]
    public async Task Screenshot_WithoutUserSelectedTarget_IsAdmittedByTheDefaultGrant()
    {
        // 不设置 Agent 目标页：走的就是「默认签发」这条路径。
        var (service, surface, _) = Arrange(selectTarget: false);

        var response = await service.ExecuteAsync(
            Descriptor(DesktopCapability.BrowserScreenshot),
            DesktopCapabilityRequest.ForScreenshot(
                new BrowserScreenshotRequest(AgentPage, DesktopPageVersion.Require(1))),
            Context("op-default-grant", BrowserCallerKind.Agent, "run-1"),
            CancellationToken.None);

        // 替身对截图返回明确失败；关键事实是调用经过了准入并到达了 surface。
        Assert.True(response.IsFailure);
        Assert.Equal(1, surface.ScreenshotCount);
    }

    [Fact]
    public async Task Screenshot_WithoutUserSelectedTarget_IsStillRejectedForASubAgent()
    {
        // 默认授权只给主 Agent：子代理必须从父任务派生权限，否则级联释放会被绕开。
        var (service, surface, _) = Arrange(selectTarget: false);

        var response = await service.ExecuteAsync(
            Descriptor(DesktopCapability.BrowserScreenshot),
            DesktopCapabilityRequest.ForScreenshot(
                new BrowserScreenshotRequest(AgentPage, DesktopPageVersion.Require(1))),
            Context("op-default-grant-sub", BrowserCallerKind.SubAgent, "run-sub-1"),
            CancellationToken.None);

        Assert.True(response.IsFailure);
        Assert.Equal(0, surface.ScreenshotCount);
    }

    [Fact]
    public async Task TabsNew_WithoutAnyPriorGrant_GetsADefaultManagementGrant()
    {
        // 管理面（新建/关闭标签、建关 context）在操作发生时**还没有页面可授权**，
        // 所以页级默认授权帮不上忙；必须走 EnsureDefaultAgentManagementGrant。
        // 断言签发本身（不等 surface 计数）：真正的裁决仍在 Authority.Admit。
        var (service, lifecycle) = ArrangeTabs();

        await service.ExecuteAsync(
            Descriptor(DesktopCapability.BrowserTabs),
            DesktopCapabilityRequest.ForTabs(BrowserTabsRequest.New("ctx-agent", null, true)),
            Context("op-default-mgmt", BrowserCallerKind.Agent, "run-mgmt-1"),
            CancellationToken.None);

        Assert.NotEmpty(lifecycle.Authority.ActiveManagementGrants);
    }

    private static (DesktopService Service, BrowserPageLifecycle Lifecycle) ArrangeTabs()
    {
        var clock = new TestTimeProvider();
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, clock);
        lifecycle.RegisterContext(AgentPage.ContextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.Authority.NotifyConnectionGeneration(Generation);

        var issuer = new BrowserGrantIssuer(lifecycle, DesktopId, ProcessId, () => Generation, clock);
        var bindings = new BrowserTaskBindingService(lifecycle, issuer, clock);

        var service = new DesktopService(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            lifecycle.PageTargets,
            new DesktopServiceOptions
            {
                AllowedCapabilities = DesktopCapability.BrowserTabs,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            },
            lifecycle.Authority,
            clock,
            () => Generation,
            bindings);

        return (service, lifecycle);
    }

    private static (DesktopService Service, RecordingUiSurface Surface, TestTimeProvider Clock) Arrange(bool selectTarget)
    {
        var clock = new TestTimeProvider();
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId, clock);

        // 必须用 lifecycle 级别的注册：Accept 读的是 _lifecycle.BrowserTargets，
        // 而服务侧读 PageTargets。只写 PageTargets 会让上下文可信级在 BrowserTargets 里缺失，
        // 默认接管分支永远不会触发（这正是 BrowserWorkspaceTargetBridge 存在的原因）。
        lifecycle.RegisterContext(AgentPage.ContextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.RegisterPage(AgentPage, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        var targets = lifecycle.PageTargets;
        if (selectTarget)
        {
            targets.SetAgentTarget(AgentPage.ContextId, AgentPage.PageId);
        }

        lifecycle.Authority.NotifyConnectionGeneration(Generation);

        var issuer = new BrowserGrantIssuer(lifecycle, DesktopId, ProcessId, () => Generation, clock);
        var bindings = new BrowserTaskBindingService(lifecycle, issuer, clock);
        var surface = new RecordingUiSurface();

        var service = new DesktopService(
            new ManualUiDispatcher { HasThreadAccess = true },
            surface,
            targets,
            new DesktopServiceOptions
            {
                AllowedCapabilities = DesktopCapability.BrowserScreenshot,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            },
            lifecycle.Authority,
            clock,
            () => Generation,
            bindings);

        return (service, surface, clock);
    }

    private static DesktopCallContext Context(string operationId, BrowserCallerKind kind, string taskId) => new(
        DesktopId,
        new OperationId(operationId),
        DateTimeOffset.UtcNow.AddSeconds(30),
        browserCaller: new BrowserCallerIdentity(
            kind,
            "caller-1",
            taskId,
            // 子代理必须携带父任务标识（BrowserCallerIdentity 构造器会拒绝缺失值）。
            parentTaskId: kind == BrowserCallerKind.SubAgent ? "parent-task-1" : null));

    private static DesktopCapabilityDescriptor Descriptor(DesktopCapability capability) =>
        DesktopCapabilities.All.Single(descriptor => descriptor.Capability == capability);
}
