using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 默认**管理面**授权（新建标签 / 建关 context）的去重判据（回归锁）。
///
/// <para>
/// 锁的是一个实测死门：<c>TabNew</c> 按 <c>BrowserOperationPolicy</c> 属 Management 类，
/// 而 <c>IssueManagementGrant</c> 在生产代码里从未被调用过 ⇒ 面板里没有页面时
/// （<c>pageCount=0</c>），Agent 既无页可驱动，也不能自己开页，只能报
/// 「no task-level management grant allows creating or closing browser targets」。
/// </para>
///
/// <para>
/// ⚠️ 与 <see cref="BrowserDefaultPageGrantPolicyTests"/> 的关键差别：这里的去重按
/// <c>IsSameTaskScope</c>（只看 CallerId）而不是 <c>IsSameActor</c>（含 runId），
/// 因为消费端 <c>BrowserAutomationAuthority.HasManagementGrant</c> 就是这么判的。
/// 两个类看起来只差一个词，判错方向的后果却是「每轮都重新签发、永远命中不到」。
/// 下面的用例把这条差别逐条钉住。
/// </para>
/// </summary>
public sealed class BrowserDefaultManagementGrantPolicyTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(7);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 23, 30, 0, TimeSpan.Zero);

    private static BrowserCallerIdentity Agent(string runId) =>
        new(BrowserCallerKind.Agent, "agent-1", runId);

    private static BrowserManagementGrant Grant(
        BrowserCallerIdentity grantee,
        DateTimeOffset? expiresAtUtc)
        => new(
            BrowserManagementGrantId.NewId(),
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"),
                new DesktopProcessInstanceId("proc-test"),
                Generation),
            grantee,
            Now.AddMinutes(-1),
            expiresAtUtc: expiresAtUtc);

    [Fact]
    public void NoGrantMeansIssue()
    {
        Assert.True(BrowserDefaultManagementGrantPolicy.ShouldIssue([], Agent("run-2"), Now));
    }

    /// <summary>
    /// 核心用例：同 CallerId、**不同 runId** 仍然算「已覆盖」。
    /// 管理面授权属于**任务**，不属于某一次运行 —— 与页级授权正好相反。
    /// </summary>
    [Fact]
    public void SameCallerDifferentRunStillCounts()
    {
        var grant = Grant(Agent("run-1"), Now.AddMinutes(1));

        Assert.False(BrowserDefaultManagementGrantPolicy.ShouldIssue([grant], Agent("run-2"), Now));

        // 对照组：同一个行动者判据在这里必然为 false，说明「跳过签发」不是靠 IsSameActor 得来的。
        Assert.False(grant.Grantee.IsSameActor(Agent("run-2")));
    }

    [Fact]
    public void ExpiredGrantMeansIssue()
    {
        var grant = Grant(Agent("run-1"), Now.AddSeconds(-1));

        Assert.True(BrowserDefaultManagementGrantPolicy.ShouldIssue([grant], Agent("run-1"), Now));
    }

    [Fact]
    public void GrantWithoutExpiryNeverExpires()
    {
        var grant = Grant(Agent("run-1"), expiresAtUtc: null);

        Assert.False(BrowserDefaultManagementGrantPolicy.ShouldIssue([grant], Agent("run-1"), Now));
    }

    [Fact]
    public void AnotherCallerDoesNotCount()
    {
        var grant = Grant(new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-2", "run-2"), Now.AddMinutes(1));

        Assert.True(BrowserDefaultManagementGrantPolicy.ShouldIssue([grant], Agent("run-1"), Now));
    }

    /// <summary>
    /// 任务作用域的**第二支判据**：CallerId 不同但 TaskId 相同时仍算同一作用域
    /// （<c>IsSameTaskScope</c> = 同 CallerId 或 同 <c>ParentTaskId ?? TaskId</c>）。
    /// 这条看起来反直觉，但它就是消费端 <c>HasManagementGrant</c> 的实际判据 ——
    /// 所以这里必须写成断言，而不是靠注释安抚读者。
    /// </summary>
    [Fact]
    public void DifferentCallerIdButSameTaskIdIsSameScope()
    {
        var grant = Grant(new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-2", "run-1"), Now.AddMinutes(1));

        Assert.False(BrowserDefaultManagementGrantPolicy.ShouldIssue([grant], Agent("run-1"), Now));
    }

    /// <summary>
    /// 子代理与父任务同 CallerId 时按任务作用域算同一份 —— 这与消费端一致（它只看 CallerId）。
    /// 之所以要断言，是因为「默认签发」根本不给子代理（判在 DesktopService 与签发器两处），
    /// 所以这里能读到子代理的授权，只会来自父任务的显式派生。
    /// </summary>
    [Fact]
    public void SubAgentSharesTaskScopeWithSameCallerId()
    {
        var subAgent = new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-1", "run-0");
        var grant = Grant(subAgent, Now.AddMinutes(1));

        Assert.False(BrowserDefaultManagementGrantPolicy.ShouldIssue([grant], Agent("run-1"), Now));
    }
}
