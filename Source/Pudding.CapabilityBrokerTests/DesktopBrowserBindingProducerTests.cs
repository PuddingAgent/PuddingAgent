using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>Core 侧签发生产者：accepted 才 Issued；拒绝带原因；通道异常不得当成功。</summary>
public sealed class DesktopBrowserBindingProducerTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly DesktopCapabilityDescriptor Apply = new(
        DesktopCapability.BrowserBindingApply, "browser.binding.apply", 1,
        DesktopCapabilityKind.Control, DesktopCapabilityTraits.RequiresTrustedContext);
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");
    private static readonly DesktopPageTarget Page = new("ctx-1", "page-1");
    private static readonly BrowserPageGrantId GrantId = new("grant-1");

    private static BrowserTaskBinding Binding() =>
        new(Agent, Page, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5), "user-goal:g-1");

    [Fact]
    public async Task AnAcceptedAckIsReportedAsIssuedWithTheGrantId()
    {
        var sent = new List<DesktopCapabilityRequest>();
        var producer = new DesktopBrowserBindingProducer(
            (capability, request, call, token) =>
            {
                sent.Add(request);
                Assert.Equal("browser.binding.apply", capability.Name);
                Assert.Equal("run-1", call.BrowserCaller!.TaskId);
                return Task.FromResult(CapabilityResult<DesktopCapabilityResponse>.Success(
                    DesktopCapabilityResponse.FromBindingAck(new BrowserBindingAck(true, GrantId, Epoch: default, Reason: null))));
            },
            DesktopId, Apply);

        var outcome = await producer.IssueAsync(Binding(), new OperationId("op-1"));

        Assert.True(outcome.Issued);
        Assert.Equal(GrantId, outcome.GrantId);
        Assert.Null(outcome.Reason);
        Assert.Single(sent);
        Assert.Equal("page-1", sent[0].BindingApply!.Binding.Target.PageId);
    }

    [Fact]
    public async Task ARejectedAckIsReportedWithItsReasonAndNoGrant()
    {
        var producer = new DesktopBrowserBindingProducer(
            (capability, request, call, token) => Task.FromResult(CapabilityResult<DesktopCapabilityResponse>.Success(
                DesktopCapabilityResponse.FromBindingAck(new BrowserBindingAck(
                    false, GrantId: null, Epoch: default,
                    Reason: "no active parent grant for this sub-agent's parent task on the requested page")))),
            DesktopId, Apply);

        var outcome = await producer.IssueAsync(Binding(), new OperationId("op-2"));

        Assert.False(outcome.Issued);
        Assert.Null(outcome.GrantId);
        Assert.Contains("parent grant", outcome.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowingChannelIsReportedAsFailureNotSuccess()
    {
        var producer = new DesktopBrowserBindingProducer(
            (capability, request, call, token) => throw new InvalidOperationException("channel died"),
            DesktopId, Apply);

        var outcome = await producer.IssueAsync(Binding(), new OperationId("op-3"));

        Assert.False(outcome.Issued);
        Assert.Null(outcome.GrantId);
        Assert.Contains("InvalidOperationException", outcome.Reason!, StringComparison.Ordinal);
    }
}