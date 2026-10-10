using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>组合根门：**无会话 ⇒ 放行（no-op）**（通道默认关闭；最终判据在 Desktop 的唯一 authority）。</summary>
public sealed class CapabilityChannelBindingGateTests
{
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");

    private static CapabilityChannelBindingGate Gate() => new(
        () => null,
        new DesktopCapabilityDescriptor(
            DesktopCapability.BrowserBindingApply, "browser.binding.apply", 1,
            DesktopCapabilityKind.Control, DesktopCapabilityTraits.RequiresTrustedContext),
        BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
        TimeSpan.FromMinutes(5),
        "bridge:agent-target");

    [Fact]
    public async Task WithoutASessionTheGateIsANoOp()
    {
        await Gate().EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None);
    }

    [Fact]
    public async Task WithoutASessionRepeatedCallsAreStillNoOps()
    {
        var gate = Gate();
        await gate.EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None);
        await gate.EnsureAsync(Agent, "ctx-1", "page-2", CancellationToken.None);
    }
}