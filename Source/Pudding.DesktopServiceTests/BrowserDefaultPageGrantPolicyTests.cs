using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 默认页级授权的**去重判据**（回归锁）。
///
/// <para>
/// 锁的是一个实测缺陷：去重曾用 <c>IsSameTaskScope</c>（只看 CallerId），
/// 而消费端 `<c>BrowserAutomationAuthority.Admit</c>` 用 <c>IsSameActor</c>（Kind+CallerId+TaskId）。
/// 主 Agent 的 TaskId 是本次 runId，每轮变化 ⇒ 上一轮的授权被「去重认为已覆盖」而跳过签发，
/// 随后被「裁决认为发给了别人」而拒绝。现象是**能读不能写**，且稳定复现。
/// </para>
///
/// 因此下面每个用例都断言「这张授权消费端会不会接受」，而不是只断言布尔值好看。
/// </summary>
public sealed class BrowserDefaultPageGrantPolicyTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(11);
    private static readonly DesktopPageTarget Page = new("ctx-1", "page-1");
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 23, 0, 0, TimeSpan.Zero);

    private static BrowserCallerIdentity Agent(string runId) =>
        new(BrowserCallerKind.Agent, "agent-1", runId);

    private static BrowserPageGrant Grant(
        DesktopPageTarget target,
        BrowserCallerIdentity grantee,
        DateTimeOffset? expiresAtUtc)
        => new(
            new BrowserPageGrantId(Guid.NewGuid().ToString("N")),
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"),
                new DesktopProcessInstanceId("proc-test"),
                Generation),
            target,
            grantee,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            Now.AddMinutes(-1),
            expiresAtUtc: expiresAtUtc);

    [Fact]
    public void NoGrantMeansIssue()
    {
        Assert.True(BrowserDefaultPageGrantPolicy.ShouldIssue([], Page, Agent("run-2"), Now));
    }

    [Fact]
    public void SameActorAndLiveMeansSkip()
    {
        var grant = Grant(Page, Agent("run-1"), Now.AddMinutes(4));

        Assert.False(BrowserDefaultPageGrantPolicy.ShouldIssue([grant], Page, Agent("run-1"), Now));
    }

    /// <summary>核心回归：同 CallerId、**不同 runId** 的授权不能拿去去重 —— 消费端不接受它。</summary>
    [Fact]
    public void SameCallerButDifferentRunMustIssueAgain()
    {
        var previousRun = Grant(Page, Agent("run-1"), Now.AddMinutes(4));

        var shouldIssue = BrowserDefaultPageGrantPolicy.ShouldIssue(
            [previousRun],
            Page,
            Agent("run-2"),
            Now);

        // 两条断言指向同一事实：跳过签发必然导致 CallerMismatch。
        Assert.True(shouldIssue);
        Assert.False(previousRun.Grantee.IsSameActor(Agent("run-2")));
    }

    [Fact]
    public void ExpiredGrantMeansIssue()
    {
        var grant = Grant(Page, Agent("run-1"), Now.AddSeconds(-1));

        Assert.True(BrowserDefaultPageGrantPolicy.ShouldIssue([grant], Page, Agent("run-1"), Now));
    }

    [Fact]
    public void GrantWithoutExpiryNeverExpires()
    {
        var grant = Grant(Page, Agent("run-1"), expiresAtUtc: null);

        Assert.False(BrowserDefaultPageGrantPolicy.ShouldIssue([grant], Page, Agent("run-1"), Now));
    }

    [Fact]
    public void GrantForAnotherPageDoesNotCount()
    {
        var otherPage = new DesktopPageTarget("ctx-1", "page-2");
        var grant = Grant(otherPage, Agent("run-1"), Now.AddMinutes(4));

        Assert.True(BrowserDefaultPageGrantPolicy.ShouldIssue([grant], Page, Agent("run-1"), Now));
    }

    /// <summary>子代理身份与 Agent 身份即便 CallerId 相同也不是同一行动者。</summary>
    [Fact]
    public void SubAgentIdentityIsNotTheAgentActor()
    {
        var subAgent = new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-1", "run-0");
        var grant = Grant(Page, subAgent, Now.AddMinutes(4));

        Assert.True(BrowserDefaultPageGrantPolicy.ShouldIssue([grant], Page, Agent("run-1"), Now));
    }
}
