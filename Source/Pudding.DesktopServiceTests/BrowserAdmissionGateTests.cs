using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>
/// 准入闸门（方案 §4.2）：注入连接世代后，浏览器能力**必须**过 authority 准入；
/// 被拒时**不得产生任何驱动调用**（"拒绝的驱动调用数=0"）。对照组保证不是"根本没接上线"。
/// </summary>
public sealed class BrowserAdmissionGateTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(7);
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");

    private static ServiceHarness Harness() => ServiceHarness.Create(
        allowed: DesktopCapability.WebViewNavigate,
        hasThreadAccess: true,
        connectionGeneration: () => Generation);

    private static DesktopCallContext Context(ServiceHarness harness, string operationId) =>
        harness.Context(operationId) with { BrowserCaller = Agent };

    private static void IssueWriteGrant(ServiceHarness harness)
    {
        var issuedAt = harness.Clock.GetUtcNow();
        harness.Service.Authority.IssueGrant(new BrowserPageGrant(
            new BrowserPageGrantId(Guid.NewGuid().ToString("N")),
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"), new DesktopProcessInstanceId("proc-test"), Generation),
            ServiceHarness.AgentPage,
            Agent,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt.AddMinutes(5)));
    }

    [Fact]
    public async Task WithoutAGrantTheCapabilityIsRefusedBeforeAnyDriverCall()
    {
        var harness = Harness();
        harness.Service.Authority.NotifyConnectionGeneration(Generation);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-denied"));

        Assert.True(response.IsFailure);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task WithAGrantTheCapabilityReachesTheDriverExactlyOnce()
    {
        var harness = Harness();
        harness.Service.Authority.NotifyConnectionGeneration(Generation);
        IssueWriteGrant(harness);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-allowed"));

        Assert.False(response.IsFailure);
        Assert.Equal(1, harness.Surface.NavigateCount);
    }
}