using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;

namespace Pudding.DesktopSurface.BrowserTests;

public sealed class BrowserTargetObservationTests
{
    private static readonly BrowserCallerIdentity Caller = new(BrowserCallerKind.Agent, "agent", "run");
    private static readonly BrowserSessionControlHandle Owner = BrowserSessionControlHandle.Require("conversation");
    private static readonly DesktopPageTarget First = new("ctx", "one");
    private static readonly DesktopPageTarget Second = new("ctx", "two");

    [Fact]
    public void InitialEmptySnapshotDoesNotRevokeImplicitControl()
    {
        var (lifecycle, bridge) = Create();
        bridge.ObserveAgentTarget("ctx", null);
        var decision = lifecycle.AuthorizePageCommand(Caller, Owner, First, requiresWrite: true);
        Assert.True(decision.Allow, decision.Message);
        bridge.ObserveAgentTarget("ctx", null);
        Assert.True(lifecycle.RevalidatePageCommand(decision.Permit!).Allow);
        lifecycle.CompletePageCommand(decision.Permit!);
    }

    [Fact]
    public void RepeatedSnapshotDoesNotRebindAnActiveOperationToTheOldPage()
    {
        var (lifecycle, bridge) = Create();
        bridge.ObserveAgentTarget("ctx", "one");
        Assert.True(lifecycle.TryClaimControlOwner(Owner.OwnerId));
        var decision = lifecycle.AuthorizePageCommand(Caller, Owner, Second, requiresWrite: true);
        Assert.True(decision.Allow, decision.Message);
        bridge.ObserveAgentTarget("ctx", "one");
        Assert.True(lifecycle.RevalidatePageCommand(decision.Permit!).Allow);
        Assert.Equal(Second, lifecycle.CaptureSessionTarget());
        lifecycle.CompletePageCommand(decision.Permit!);
    }

    [Fact]
    public void ClosingObservedPageInvalidatesItsPermitWithoutSuspendingTheNextPage()
    {
        var (lifecycle, bridge) = Create();
        bridge.ObserveAgentTarget("ctx", "one");
        Assert.True(lifecycle.TryClaimControlOwner(Owner.OwnerId));
        var first = lifecycle.AuthorizePageCommand(Caller, Owner, First, requiresWrite: true);
        Assert.True(first.Allow);
        bridge.OnPageClosed("ctx", "one");
        bridge.ObserveAgentTarget("ctx", null);
        Assert.False(lifecycle.RevalidatePageCommand(first.Permit!).Allow);
        var second = lifecycle.AuthorizePageCommand(Caller, Owner, Second, requiresWrite: true);
        Assert.True(second.Allow, second.Message);
        lifecycle.CompletePageCommand(second.Permit!);
    }

    [Fact]
    public void EmptySnapshotCannotUndoExplicitRevocation()
    {
        var (lifecycle, bridge) = Create();
        var first = lifecycle.AuthorizePageCommand(Caller, Owner, First, requiresWrite: true);
        Assert.True(first.Allow);
        bridge.OnAgentTargetChanged(null, null);
        bridge.ObserveAgentTarget("ctx", null);
        Assert.False(lifecycle.RevalidatePageCommand(first.Permit!).Allow);
        Assert.False(lifecycle.AuthorizePageCommand(Caller, Owner, First, requiresWrite: true).Allow);
    }

    private static (BrowserPageLifecycle, BrowserWorkspaceTargetBridge) Create()
    {
        var lifecycle = BrowserPageLifecycle.Create(new DesktopInstanceId("desktop"), new DesktopProcessInstanceId("process"));
        lifecycle.NotifyBridgeConnected(1);
        var bridge = new BrowserWorkspaceTargetBridge(lifecycle);
        bridge.OnContextCreated("ctx");
        bridge.OnPageCreated("ctx", "one", DesktopPageVersion.Require(1));
        bridge.OnPageCreated("ctx", "two", DesktopPageVersion.Require(1));
        return (lifecycle, bridge);
    }
}
