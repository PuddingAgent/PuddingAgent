using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// page 级准入闸门（审计 2026-10-08 结论 #2 / 设计 §3.2）。
///
/// 被钉住的事实：可信级别是 <b>context 级</b>，所以「在 AgentAuthorized 上下文里」曾经等价于
/// 「这个页面已获授权」——同一上下文的**非目标页**同样能通过准入，UI 上的「Agent 目标」只是暗示。
/// 现在要求页面必须是用户选定的 Agent 目标页。
/// </summary>
public sealed class AgentTargetAdmissionTests
{
    private const DesktopCapability BrowserCapabilities =
        DesktopCapability.BrowserSnapshot
        | DesktopCapability.BrowserLocate
        | DesktopCapability.BrowserInteract
        | DesktopCapability.BrowserWaitFor
        | DesktopCapability.BrowserTabs
        | DesktopCapability.BrowserContexts
        | DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewPageState
        | DesktopCapability.WebViewExecuteJavascript;

    private static DesktopCapabilityRequest SnapshotRequest(DesktopPageTarget target, DesktopPageVersion version) =>
        DesktopCapabilityRequest.ForSnapshot(new BrowserSnapshotRequest(target, version));

    [Fact]
    public async Task SiblingPage_InTheSameAgentAuthorizedContext_IsDenied()
    {
        // 这就是审计结论 #2 的复现：同一 AgentAuthorized 上下文里的非目标页。
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);
        var sibling = new DesktopPageTarget(ServiceHarness.AgentPage.ContextId, "page-2");
        harness.Targets.RegisterPage(sibling, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot, SnapshotRequest(sibling, DesktopPageVersion.Require(1)));

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, response.Error!.Code);
        // 拒绝必须发生在触碰 UI 之前：驱动调用数为 0。
        Assert.Equal(0, harness.Surface.SnapshotCount);
    }

    [Fact]
    public async Task TheDesignatedAgentTarget_IsAllowed()
    {
        // 控制组：闸门不能让正例也失败，否则「拒绝」不再是有效证据。
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            SnapshotRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(1)));

        Assert.False(response.IsFailure);
        Assert.Equal(1, harness.Surface.SnapshotCount);
    }

    [Fact]
    public async Task MovingTheAgentTarget_MovesTheGate()
    {
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);
        var second = new DesktopPageTarget(ServiceHarness.AgentPage.ContextId, "page-2");
        harness.Targets.RegisterPage(second, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);

        Assert.True(harness.Targets.SetAgentTarget(second.ContextId, second.PageId));

        var allowed = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot, SnapshotRequest(second, DesktopPageVersion.Require(1)));
        Assert.False(allowed.IsFailure);

        // 旧目标页随切换一起失去授权（同一时刻至多一个目标）。
        var denied = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            SnapshotRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(1)));
        Assert.True(denied.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, denied.Error!.Code);
    }

    [Fact]
    public async Task ClearingTheAgentTarget_DeniesPreviouslyAllowedCapabilities()
    {
        // 用户接管 / 会话结束会把目标清空 ⇒ 必须 fail closed，而不是保留上一次的授权。
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);
        Assert.False(harness.Targets.SetAgentTarget(null, null));

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            SnapshotRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(1)));

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, response.Error!.Code);
        Assert.Equal(0, harness.Surface.SnapshotCount);
    }

    [Fact]
    public async Task GeneralPurposeBrowsing_IsNotGatedByTheAgentTarget()
    {
        // 不做过头：page_state 与 navigate 对普通网页/工作台也开放，属通用浏览能力。
        // 把它们也收紧到「目标页」属 grant 模型切片，本闸门刻意不含它们。
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);

        var pageState = await harness.ExecuteAsync(
            DesktopCapability.WebViewPageState,
            DesktopCapabilityRequest.ForPageState(ServiceHarness.WebPage));

        Assert.False(pageState.IsFailure);
        Assert.Equal(1, harness.Surface.PageStateCount);
    }

    [Fact]
    public async Task ContextScopedBrowserCapabilities_AreUnaffected()
    {
        // 无页面目标的能力没有「哪个页面」可判，仍只由 context 可信级别把关。
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);

        var contexts = await harness.ExecuteAsync(
            DesktopCapability.BrowserContexts, DesktopCapabilityRequest.ForContexts());

        Assert.False(contexts.IsFailure);
        Assert.Equal(1, harness.Surface.ContextsCount);
    }

    [Fact]
    public async Task ScriptInjection_RequiresTheAgentTarget()
    {
        // 脚本注入只对获授权的 Agent 浏览器开放，因此必须落在**目标页**上。
        var harness = ServiceHarness.Create(allowed: BrowserCapabilities, hasThreadAccess: true);
        var sibling = new DesktopPageTarget(ServiceHarness.AgentPage.ContextId, "page-2");
        harness.Targets.RegisterPage(sibling, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);

        var denied = await harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript,
            harness.JavascriptRequest(sibling));

        Assert.True(denied.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, denied.Error!.Code);
        Assert.Equal(0, harness.Surface.JavascriptCount);

        var allowed = await harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript,
            harness.JavascriptRequest(ServiceHarness.AgentPage));

        Assert.False(allowed.IsFailure);
        Assert.Equal(1, harness.Surface.JavascriptCount);
    }
}
