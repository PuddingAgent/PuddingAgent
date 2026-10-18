using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Xunit;

namespace DesktopServiceTests;

/// <summary>
/// I2：浏览器会话准入进入**生产生命周期端口**后的不变量（ADR-097 D3/D4）。
///
/// 与组件测试的分工：组件测试证明状态机本身；这里证明 <see cref="BrowserPageLifecycle"/>
/// 的标量门面（Shell/桥侧唯一可见面）真的把用户交页、可信 owner、运行控制与关闭路径接进了
/// 同一个权威，并且拒绝码与线缆码一致。
/// </summary>
public sealed class BrowserSessionAdmissionTests
{
    private static readonly DesktopPageTarget PageA = new("ctx-1", "page-a");
    private static readonly DesktopPageTarget PageB = new("ctx-1", "page-b");

    private static readonly BrowserCallerIdentity Caller = new(
        BrowserCallerKind.Agent, "agent-a", "run-agent-a");

    [Fact]
    public void HandoverIsAdmittedWithoutAnExplicitClaimUnderV1()
    {
        var lifecycle = ReadyLifecycle();

        lifecycle.SetAgentTarget(PageA.ContextId, PageA.PageId);

        // V1：内置浏览器只服务本机 Agent —— 不再要求“先确认归属再准入”，首个可信命令即建立会话。
        var allowed = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: true);
        Assert.True(allowed.Allow, $"error={allowed.ErrorCode}: {allowed.Message}");
        Assert.NotNull(allowed.Permit);
        Assert.True(lifecycle.CaptureSessionStatus().HasTarget);
    }

    [Fact]
    public void AnotherOwnerCannotUseOrClaimTheHandedOverPage()
    {
        var lifecycle = ReadyLifecycle();
        HandOverAndClaim(lifecycle, "session-a");

        Assert.False(lifecycle.TryClaimControlOwner("session-b"));
        var denied = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-b"), PageA, requiresWrite: false);

        Assert.False(denied.Allow);
        Assert.Equal(BrowserSessionErrorCodes.OwnerMismatch, denied.ErrorCode);
    }

    [Fact]
    public void PauseBlocksWritesButKeepsReadsAndCancelsTheInFlightWrite()
    {
        var lifecycle = ReadyLifecycle();
        HandOverAndClaim(lifecycle, "session-a");

        var write = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: true);
        Assert.True(write.Allow);

        lifecycle.SetPaused(true, "user pressed pause");

        Assert.False(lifecycle.RevalidatePageCommand(write.Permit!).Allow);
        lifecycle.CompletePageCommand(write.Permit!);

        var read = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: false);
        Assert.True(read.Allow, "暂停是运行控制：只读观察必须继续");

        var blockedWrite = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: true);
        Assert.False(blockedWrite.Allow);
        Assert.Equal(BrowserSessionErrorCodes.Busy, blockedWrite.ErrorCode);
    }

    [Fact]
    public void TargetSwitchRevokesTheOldPermitsButTheNextCommandRebinds()
    {
        var lifecycle = ReadyLifecycle();
        lifecycle.RegisterPage(PageB, DesktopPageVersion.Require(2), DesktopPageReadiness.Interactive);
        HandOverAndClaim(lifecycle, "session-a");

        var permit = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: true).Permit!;

        lifecycle.SetAgentTarget(PageB.ContextId, PageB.PageId);

        // 旧票据跳目标切换必然失效（正确性不变）。
        Assert.False(lifecycle.RevalidatePageCommand(permit).Allow);

        // V1：新命令以请求页重新绑定，不再因“不是当前目标页”被永久拒绝。
        Assert.True(lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: false).Allow);
    }

    [Fact]
    public void ClosingTheTargetPageRevokesTheSession()
    {
        var lifecycle = ReadyLifecycle();
        HandOverAndClaim(lifecycle, "session-a");

        lifecycle.ClosePage(PageA);

        var status = lifecycle.CaptureSessionStatus();
        Assert.False(status.HasTarget);
        Assert.False(status.HasPendingHandover);
    }

    [Fact]
    public void BridgeLossRevokesTheSession()
    {
        var lifecycle = ReadyLifecycle();
        HandOverAndClaim(lifecycle, "session-a");

        lifecycle.NotifyBridgeDisconnected();

        Assert.False(lifecycle.CaptureSessionStatus().HasTarget);
    }

    [Fact]
    public void ReleasingAPermitTwiceIsIdempotentAndThePermitCannotBeReused()
    {
        var lifecycle = ReadyLifecycle();
        HandOverAndClaim(lifecycle, "session-a");

        var permit = lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: true).Permit!;

        lifecycle.CompletePageCommand(permit);
        lifecycle.CompletePageCommand(permit);

        Assert.False(lifecycle.RevalidatePageCommand(permit).Allow);
        Assert.True(lifecycle.AuthorizePageCommand(
            Caller, BrowserSessionControlHandle.Require("session-a"), PageA, requiresWrite: true).Allow,
            "释放后写槽必须释放，后续写可用");
    }

    [Fact]
    public void TheWireErrorCodesMatchTheComponentContract()
    {
        // 桥侧只能看到 DesktopService 的常量；两边任一改动都必须在同一次提交里同步，否则客户端会收到不认识的码。
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.TransportUnavailable, BrowserSessionErrorCodes.TransportUnavailable);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.TargetRequired, BrowserSessionErrorCodes.TargetRequired);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.TargetRevoked, BrowserSessionErrorCodes.TargetRevoked);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.UserConfirmationRequired, BrowserSessionErrorCodes.UserConfirmationRequired);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.Busy, BrowserSessionErrorCodes.Busy);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.Closed, BrowserSessionErrorCodes.Closed);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.ControlHandleRequired, BrowserSessionErrorCodes.ControlHandleRequired);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.OwnerMismatch, BrowserSessionErrorCodes.OwnerMismatch);
        Assert.Equal(PuddingBrowser.Authorization.AgentTargetErrorCodes.PermitInvalid, BrowserSessionErrorCodes.PermitInvalid);
    }

    private static void HandOverAndClaim(BrowserPageLifecycle lifecycle, string ownerId)
    {
        lifecycle.SetAgentTarget(PageA.ContextId, PageA.PageId);
        Assert.True(lifecycle.TryClaimControlOwner(ownerId));
    }

    private static BrowserPageLifecycle ReadyLifecycle()
    {
        var lifecycle = BrowserPageLifecycle.Create(
            new DesktopInstanceId("desk-test"),
            new DesktopProcessInstanceId("proc-test"));
        lifecycle.NotifyBridgeConnected(41);
        lifecycle.RegisterContext(PageA.ContextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.RegisterPage(PageA, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        return lifecycle;
    }
}
