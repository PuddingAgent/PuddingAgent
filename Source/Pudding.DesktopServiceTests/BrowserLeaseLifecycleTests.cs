using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>
/// 变更类操作的**租约生命周期**（round-229 修的缺陷）：准入拿到的租约必须在操作结束时释放，
/// 否则第一次写之后，后续写会被自己留下的租约挡住（用户接管语义也随之失真）。
/// </summary>
public sealed class BrowserLeaseLifecycleTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(21);
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");

    private static ServiceHarness Harness() => ServiceHarness.Create(
        allowed: DesktopCapability.WebViewNavigate,
        hasThreadAccess: true,
        connectionGeneration: () => Generation);

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

    private static DesktopCallContext Context(ServiceHarness harness, string operationId) =>
        harness.Context(operationId) with { BrowserCaller = Agent };

    [Fact]
    public async Task AWriteOperationReleasesItsLeaseSoTheNextWriteStillWorks()
    {
        var harness = Harness();
        harness.Service.Authority.NotifyConnectionGeneration(Generation);
        IssueWriteGrant(harness);

        var first = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-write-1"));

        Assert.False(first.IsFailure);

        // 关键判据①：操作结束后权威里**不该还挂着**本操作的租约。
        Assert.Null(harness.Service.Authority.Capture().ActiveLease);

        var second = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-write-2"));

        // 关键判据②：第二次写仍然成功（修复前会被第一次留下的租约挡住）。
        Assert.False(second.IsFailure);
        Assert.Equal(2, harness.Surface.NavigateCount);
    }
}