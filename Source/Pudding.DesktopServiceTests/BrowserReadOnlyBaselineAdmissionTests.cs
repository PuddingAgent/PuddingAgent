using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Xunit;

namespace DesktopServiceTests;

/// <summary>
/// F1 的执行侧前提：默认 Bridge（无 grant 签发者）下，**截图（只读）**必须能经执行器到达 UI 缝隙。
///
/// 旧的 authority 页级 grant 准入在 Bridge 默认路径没有来源；本切片允许的唯一例外是
/// 「只读 + 用户选中的 Agent 目标页」（页级基线，不签发租约）。写操作**不得**借用这条基线。
/// </summary>
public sealed class BrowserReadOnlyBaselineAdmissionTests
{
    private static readonly DesktopPageTarget AgentPage = new("ctx-agent", "page-1");

    [Fact]
    public async Task Screenshot_ReachesTheSurface_WithOnlyTheUserSelectedTargetPage()
    {
        var (_, surface, executor) = CreateReady(selectTarget: true);

        var response = await executor.ExecuteAsync(
            Descriptor(DesktopCapability.BrowserScreenshot),
            DesktopCapabilityRequest.ForScreenshot(
                new BrowserScreenshotRequest(AgentPage, DesktopPageVersion.Require(3))),
            Context("op-screenshot"),
            CancellationToken.None);

        // 替身 surface 对截图返回明确失败：关键事实是调用**真的**经过了准入并到达 UI 缝隙。
        Assert.True(response.IsFailure);
        Assert.Equal(1, surface.ScreenshotCount);

        await executor.DisposeAsync();
    }

    [Fact]
    public async Task Screenshot_WithoutAUserSelectedTarget_IsRejectedBeforeTheSurface()
    {
        var (_, surface, executor) = CreateReady(selectTarget: false);

        var response = await executor.ExecuteAsync(
            Descriptor(DesktopCapability.BrowserScreenshot),
            DesktopCapabilityRequest.ForScreenshot(
                new BrowserScreenshotRequest(AgentPage, DesktopPageVersion.Require(3))),
            Context("op-screenshot"),
            CancellationToken.None);

        Assert.True(response.IsFailure);
        Assert.Equal(0, surface.ScreenshotCount);

        await executor.DisposeAsync();
    }

    [Fact]
    public async Task MutatingCapability_CannotBorrowTheReadOnlyBaseline()
    {
        var (_, surface, executor) = CreateReady(selectTarget: true);

        var response = await executor.ExecuteAsync(
            Descriptor(DesktopCapability.WebViewNavigate),
            DesktopCapabilityRequest.ForNavigate(new NavigateRequest(
                AgentPage, new Uri("https://example.com"), DesktopPageVersion.Require(3))),
            Context("op-navigate"),
            CancellationToken.None);

        Assert.True(response.IsFailure);
        Assert.Equal(0, surface.NavigateCount);

        await executor.DisposeAsync();
    }

    private static (BrowserPageLifecycle Lifecycle, RecordingUiSurface Surface, IDesktopBrowserExecutor Executor)
        CreateReady(bool selectTarget)
    {
        var lifecycle = TestAuthorities.CreateLifecycle();
        lifecycle.NotifyBridgeConnected(7);
        lifecycle.RegisterContext(AgentPage.ContextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.RegisterPage(
            AgentPage,
            DesktopPageVersion.Require(3),
            DesktopPageReadiness.Interactive);

        if (selectTarget)
        {
            lifecycle.SetAgentTarget(AgentPage.ContextId, AgentPage.PageId);
        }

        var surface = new RecordingUiSurface();
        var executor = DesktopBrowserExecutorComposition.Create(
            new ManualUiDispatcher { HasThreadAccess = true },
            surface,
            lifecycle,
            options: new DesktopServiceOptions
            {
                AllowedCapabilities = DesktopCapability.BrowserScreenshot | DesktopCapability.WebViewNavigate,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            });

        return (lifecycle, surface, executor);
    }

    private static DesktopCapabilityDescriptor Descriptor(DesktopCapability capability) =>
        DesktopCapabilities.All.Single(descriptor => descriptor.Capability == capability);

    private static DesktopCallContext Context(string operationId) => new(
        new DesktopInstanceId("desk-test"),
        new OperationId(operationId),
        DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30),
        browserCaller: new BrowserCallerIdentity(
            BrowserCallerKind.Agent, "agent-test", "task-test"));
}
