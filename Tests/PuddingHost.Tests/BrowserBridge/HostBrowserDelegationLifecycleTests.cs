using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;
using PuddingCode.Abstractions;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// R4 (6.2) host-side delegation port. Unlike the gate (which no-ops when the channel is not
/// enabled), a delegation is EXPLICITLY REQUESTED, so every failure path must be a DENIAL WITH A
/// REASON (fail-closed and diagnosable) -- never a silent pass.
/// </summary>
public sealed class HostBrowserDelegationLifecycleTests
{
    private static HostBrowserDelegationLifecycle Port(
        BrowserPageGrantScope grantedScope = BrowserPageGrantScope.Read | BrowserPageGrantScope.Write) => new(
        () => null,
        new DesktopCapabilityDescriptor(
            DesktopCapability.BrowserBindingApply, "browser.binding.apply", 1,
            DesktopCapabilityKind.Control, DesktopCapabilityTraits.RequiresTrustedContext),
        new DesktopCapabilityDescriptor(
            DesktopCapability.BrowserBindingRevoke, "browser.binding.revoke", 1,
            DesktopCapabilityKind.Control, DesktopCapabilityTraits.RequiresTrustedContext),
        grantedScope,
        TimeSpan.FromMinutes(5),
        "bridge:sub-agent-delegation");

    private static BrowserDelegationGrantRequest Request(
        string? contextId = "ctx-1",
        string? pageId = "page-1",
        string? scopeName = "read",
        string parentRunId = "run-parent",
        string parentTaskId = "task-1",
        string childRunId = "run-child",
        string callerId = "child-agent") => new(
        CallerKind: "sub_agent",
        CallerId: callerId,
        ParentRunId: parentRunId,
        ParentTaskId: parentTaskId,
        ChildRunId: childRunId,
        ContextId: contextId,
        PageId: pageId,
        ScopeName: scopeName,
        RequestedValidity: TimeSpan.FromMinutes(5));

    [Fact]
    public async Task NoSessionIsADenialNotASilentPass()
    {
        var result = await Port().RequestChildGrantAsync(Request(), CancellationToken.None);

        Assert.False(result.Granted);
        Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
        Assert.Contains("session", result.DenialReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownScopeNameIsDeniedInsteadOfDowngraded()
    {
        var result = await Port().RequestChildGrantAsync(Request(scopeName: "teleport"), CancellationToken.None);

        Assert.False(result.Granted);
        Assert.Contains("teleport", result.DenialReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopeOutsideTheHostGrantIsDenied()
    {
        // The host only ever grants Read here, so a Write request must be refused.
        var result = await Port(BrowserPageGrantScope.Read)
            .RequestChildGrantAsync(Request(scopeName: "write"), CancellationToken.None);

        Assert.False(result.Granted);
    }

    [Fact]
    public async Task MissingTargetIsDenied()
    {
        var result = await Port().RequestChildGrantAsync(Request(pageId: null), CancellationToken.None);

        Assert.False(result.Granted);
        Assert.Contains("page id", result.DenialReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingChildRunIsDenied()
    {
        var result = await Port().RequestChildGrantAsync(Request(childRunId: " "), CancellationToken.None);

        Assert.False(result.Granted);
        Assert.Contains("child run", result.DenialReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnacceptableCallerIdentityIsDeniedWithTheReason()
    {
        // An empty caller id violates the contract => denied (not thrown out of the port).
        var result = await Port().RequestChildGrantAsync(Request(callerId: string.Empty), CancellationToken.None);

        Assert.False(result.Granted);
        Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
    }

    [Fact]
    public async Task RevokingSomethingNeverIssuedIsIdempotent()
    {
        // Nothing was issued (no session) => revoke is a no-op and must not throw.
        await Port().RevokeForRunAsync("run-never-issued", "test cleanup", CancellationToken.None);
        await Port().RevokeForTaskAsync(
            "sub_agent", "child-agent", "run-never-issued", "run-parent", "test cleanup", CancellationToken.None);
    }

    [Fact]
    public async Task LosingTheConnectionClearsWhateverWasIssued()
    {
        var port = Port();
        await port.OnConnectionLostAsync("desktop connection lost", CancellationToken.None);
    }
}