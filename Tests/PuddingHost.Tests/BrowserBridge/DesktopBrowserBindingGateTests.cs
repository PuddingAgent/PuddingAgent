using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// 触发点门的语义（§4.1）：首次申请、有效期内不重发、不同页面分别申请、**拒绝即抛**（fail-closed）。
/// </summary>
public sealed class DesktopBrowserBindingGateTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");
    private static readonly BrowserPageGrantId GrantId = new("grant-1");

    private static DesktopCapabilityDescriptor Apply() => new(
        DesktopCapability.BrowserBindingApply, "browser.binding.apply", 1,
        DesktopCapabilityKind.Control, DesktopCapabilityTraits.RequiresTrustedContext);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DesktopCapabilityResponse Accepted() =>
        DesktopCapabilityResponse.FromBindingAck(new BrowserBindingAck(true, GrantId, Epoch: default, Reason: null));

    private static DesktopCapabilityResponse Rejected(string reason) =>
        DesktopCapabilityResponse.FromBindingAck(new BrowserBindingAck(false, GrantId: null, Epoch: default, Reason: reason));

    private static DesktopBrowserBindingGate Gate(
        List<DesktopCapabilityRequest> sent, TimeProvider clock, DesktopCapabilityResponse reply)
    {
        var producer = new DesktopBrowserBindingProducer(
            (capability, request, call, token) =>
            {
                sent.Add(request);
                return Task.FromResult(CapabilityResult<DesktopCapabilityResponse>.Success(reply));
            },
            DesktopId,
            Apply(),
            clock);

        return new DesktopBrowserBindingGate(
            producer,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            TimeSpan.FromMinutes(5),
            "bridge:agent-target",
            clock);
    }

    [Fact]
    public async Task TheFirstCommandOnAPageIssuesOnceAndCarriesTheBinding()
    {
        var sent = new List<DesktopCapabilityRequest>();
        var gate = Gate(sent, new FixedClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)), Accepted());

        await gate.EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None);

        var binding = Assert.Single(sent).BindingApply!.Binding;
        Assert.Equal("ctx-1", binding.Target.ContextId);
        Assert.Equal("page-1", binding.Target.PageId);
        Assert.Equal("run-1", binding.Task.TaskId);
        Assert.Equal("bridge:agent-target", binding.Source);
    }

    [Fact]
    public async Task ASecuentialCommandOnTheSamePageDoesNotReapply()
    {
        var sent = new List<DesktopCapabilityRequest>();
        var gate = Gate(sent, new FixedClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)), Accepted());

        await gate.EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None);
        await gate.EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None);

        Assert.Single(sent); // 有效期内不重复申请
    }

    [Fact]
    public async Task ADifferentPageIsAppliedSeparately()
    {
        var sent = new List<DesktopCapabilityRequest>();
        var gate = Gate(sent, new FixedClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)), Accepted());

        await gate.EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None);
        await gate.EnsureAsync(Agent, "ctx-1", "page-2", CancellationToken.None);

        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task ARefusalThrowsSoTheBrowserCommandIsNeverSent()
    {
        var sent = new List<DesktopCapabilityRequest>();
        var gate = Gate(sent, new FixedClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)),
            Rejected("the binding target is not the page the user currently selected for the agent"));

        var ex = await Assert.ThrowsAsync<BrowserOperationException>(
            () => gate.EnsureAsync(Agent, "ctx-1", "page-1", CancellationToken.None));

        Assert.Contains("not the page the user currently selected", ex.Message, StringComparison.Ordinal);
        Assert.Single(sent); // 只发了申请，**没有**发浏览器命令（本用例只到门这一层）
    }
}