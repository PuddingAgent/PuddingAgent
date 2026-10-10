using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// §3.2：`DesktopCallContext` 携带**类型化 caller**。
/// 非浏览器 Shell 调用可以没有它；**浏览器自动化操作**必须带可信 Agent/SubAgent 且运行字段完整，
/// 且**不能**用 User kind 绕过闸门。
/// </summary>
public sealed class DesktopCallContextCallerTests
{
    private static DesktopCallContext Context(BrowserCallerIdentity? caller) => new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UnixEpoch.AddMinutes(5),
        correlationId: null,
        browserCaller: caller);

    [Fact]
    public void ANonBrowserCallMayOmitTheCaller()
    {
        // 通用 Desktop 上下文：Shell 通知之类的非浏览器能力不需要 browser caller。
        var context = Context(null);
        Assert.Null(context.BrowserCaller);
    }

    [Fact]
    public void AnAgentCallerWithARunIdentityIsAcceptedForBrowserOperations()
    {
        var context = Context(new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1"));

        Assert.True(context.TryGetBrowserCaller(out var caller, out var reason), reason);
        Assert.Equal("run-1", caller.TaskId);
    }

    [Fact]
    public void ASubAgentCallerKeepsItsParentTask()
    {
        var context = Context(new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-sub", "run-parent"));

        Assert.True(context.TryGetBrowserCaller(out var caller, out var reason), reason);
        Assert.Equal("run-parent", caller.ParentTaskId);
    }

    [Fact]
    public void AMissingCallerIsRefusedWithAConcreteReason()
    {
        var context = Context(null);

        Assert.False(context.TryGetBrowserCaller(out _, out var reason));
        Assert.Contains("no trusted browser caller", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AUserCallerCannotBeUsedToBypassTheAutomationGate()
    {
        // 用户原生操作有独立可信入口；浏览器工具不得"自称 User"绕过准入。
        var context = Context(new BrowserCallerIdentity(BrowserCallerKind.User, "desktop-ui", "run-1"));

        Assert.False(context.TryGetBrowserCaller(out _, out var reason));
        Assert.Contains("cannot be admitted as a User caller", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallerWithoutARunIdentityIsRefused()
    {
        var context = Context(new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1"));

        Assert.False(context.TryGetBrowserCaller(out _, out var reason));
        Assert.Contains("no run identity", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityTypeItselfRejectsBlankOverlongAndImpossibleIdentifiers()
    {
        // §3.2：空必填、过长、不合法标识都拒绝（类型层强制，wire 解码不能靠默认值补身份）。
        Assert.Throws<ArgumentException>(() => new BrowserCallerIdentity(BrowserCallerKind.Agent, "   ", "run-1"));
        Assert.Throws<ArgumentException>(() => new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "   "));
        Assert.Throws<ArgumentException>(() => new BrowserCallerIdentity(
            BrowserCallerKind.Agent, "agent-1", new string('x', BrowserCallerIdentity.MaxLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserCallerIdentity((BrowserCallerKind)99, "agent-1", "run-1"));

        // 子代理没有父任务在类型层就不可能构造出来（不是"到了准入才发现"）。
        Assert.Throws<ArgumentException>(() => new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-sub"));
    }
}
