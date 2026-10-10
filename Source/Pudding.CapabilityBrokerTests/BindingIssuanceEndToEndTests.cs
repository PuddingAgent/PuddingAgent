using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerTests;

/// <summary>
/// 控制面**端到端**：生产者 → 真实 `DesktopSession.SendAsync` → 假 Desktop 回带 ack 的结果帧 → 生产者如实还原。
/// 这是"ack 真的能回传"的最终证据（round-250 曾发现 ack 上不了线）。
/// 注意：夹具必须**声明**控制面能力，否则命令在发送前就被挡下（round-253 的失败根因）。
/// </summary>
public sealed class BindingIssuanceEndToEndTests
{
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");
    private static readonly DesktopPageTarget Page = new("ctx-1", "page-1");

    private static DesktopCapabilityDescriptor Apply()
    {
        Assert.True(DesktopCapabilities.TryGetByName("browser.binding.apply", out var descriptor));
        return descriptor;
    }

    private static BrowserTaskBinding Binding() =>
        new(Agent, Page, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5), "user-goal:g-1");

    private static async Task<BrokerHarness> HarnessAsync()
    {
        var options = new HarnessOptions();
        options.Declared |= DesktopCapability.BrowserBindingApply | DesktopCapability.BrowserBindingRevoke;
        options.Grantable |= DesktopCapability.BrowserBindingApply | DesktopCapability.BrowserBindingRevoke;
        return await BrokerHarness.StartAsync(options);
    }

    private static DesktopBrowserBindingProducer Producer(BrokerHarness harness) =>
        new(
            (capability, request, call, cancellationToken) =>
                harness.Session.SendAsync(capability, request, call, cancellationToken),
            harness.Session.DesktopId,
            Apply());

    private static Proto.DesktopFrame AckFrame(string operationId, bool accepted, string grantId, string reason) => new()
    {
        Result = new Proto.OperationResult
        {
            OperationId = operationId,
            Generation = 1,
            BindingAck = new Proto.BindingAckOutcome
            {
                Accepted = accepted,
                GrantId = grantId,
                Epoch = 0,
                Reason = reason,
            },
        },
    };

    [Fact]
    public async Task AnAcceptedAckTravelsFromTheSessionBackToTheProducer()
    {
        await using var harness = await HarnessAsync();
        var producer = Producer(harness);

        var pending = producer.IssueAsync(Binding(), new OperationId("op-bind-ok"));

        var command = await harness.WaitForCommandAsync("op-bind-ok");
        Assert.NotNull(command);
        Assert.Equal("browser.binding.apply", command!.Capability);
        Assert.Equal("run-1", command.Caller.TaskId); // 身份随命令一起走

        harness.Channel.Push(AckFrame("op-bind-ok", accepted: true, grantId: "grant-1", reason: string.Empty));

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.Issued, outcome.Reason);
        Assert.Equal("grant-1", outcome.GrantId!.Value);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public async Task ARejectedAckTravelsBackWithItsReason()
    {
        await using var harness = await HarnessAsync();
        var producer = Producer(harness);

        var pending = producer.IssueAsync(Binding(), new OperationId("op-bind-no"));
        await harness.WaitForCommandAsync("op-bind-no");

        harness.Channel.Push(AckFrame(
            "op-bind-no", accepted: false, grantId: string.Empty,
            reason: "the binding target is not the page the user currently selected for the agent"));

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Issued);
        Assert.Null(outcome.GrantId);
        Assert.Contains("not the page the user currently selected", outcome.Reason!, StringComparison.Ordinal);
    }
}