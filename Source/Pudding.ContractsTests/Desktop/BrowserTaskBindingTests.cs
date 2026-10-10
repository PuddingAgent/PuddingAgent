using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 任务绑定（§4.1）：grantee 必须是**可信任务身份**（复用唯一规则），范围非空、期限为正、来源非空。
/// 这些都是"结构层"的拒绝；Desktop 侧策略（上限/目标已登记/世代存活/子代理子集）在签发时判。
/// </summary>
public sealed class BrowserTaskBindingTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    private static BrowserCallerIdentity Agent() => new(BrowserCallerKind.Agent, "agent-1", "run-1");

    [Fact]
    public void ATrustedTaskIdentityIsAccepted()
    {
        var binding = new BrowserTaskBinding(
            Agent(), Target, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5), "user-goal:g-1");

        Assert.Equal("run-1", binding.Task.TaskId);
        Assert.Equal(Target, binding.Target);
        Assert.Equal("user-goal:g-1", binding.Source);
    }

    [Fact]
    public void AUserCallerCannotBeTheGrantee()
    {
        // 用户不是"被授权的任务"；把 User 当 grantee 会让"用户操作"与"Agent 授权"混为一谈。
        var user = new BrowserCallerIdentity(BrowserCallerKind.User, "desktop-ui", "run-1");

        var ex = Assert.Throws<ArgumentException>(() => new BrowserTaskBinding(
            user, Target, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5), "user-goal:g-1"));
        Assert.Contains("User caller", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStructureThatGrantsNothingOrForeverIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new BrowserTaskBinding(
            Agent(), Target, BrowserPageGrantScope.None, TimeSpan.FromMinutes(5), "user-goal:g-1"));

        Assert.Throws<ArgumentException>(() => new BrowserTaskBinding(
            Agent(), Target, BrowserPageGrantScope.Read, TimeSpan.Zero, "user-goal:g-1"));

        Assert.Throws<ArgumentException>(() => new BrowserTaskBinding(
            Agent(), Target, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(-1), "user-goal:g-1"));

        // 来源不能是空白（审计要求可追溯到具体绑定）。
        Assert.Throws<ArgumentException>(() => new BrowserTaskBinding(
            Agent(), Target, BrowserPageGrantScope.Read, TimeSpan.FromMinutes(5), "   "));
    }
}
