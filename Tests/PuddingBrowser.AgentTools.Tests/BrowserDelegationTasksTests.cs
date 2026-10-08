using PuddingBrowser.AgentTools;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AgentTools.Tests;

/// <summary>
/// 浏览器子代理任务的信封工厂（B6 / G10）：工具清单来自**代码事实**，
/// 不能靠调用方拼名字 —— 那类错误编译期发现不了。
/// </summary>
public sealed class BrowserDelegationTasksTests
{
    private static readonly BrowserDelegationScope FullParent = new(CanRead: true, CanWrite: true, CanManage: true);

    [Fact]
    public void AReadOnlyBrowserTaskRequiresSnapshotAndInjectsTheRealToolIds()
    {
        var envelope = BrowserDelegationTasks.Create(
            goal: "读出这一页的价格",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            pageGrantId: "grant-1",
            expectedResult: "价格与来源 URL",
            deadline: TimeSpan.FromMinutes(3));

        Assert.Contains(BrowserAgentToolIds.Snapshot, envelope.RequiredTools);

        // 注入列表用真实工具 ID（不是调用方手写的字符串）。
        foreach (var tool in BrowserAgentToolIds.All)
        {
            Assert.Contains(tool, envelope.InjectedTools);
        }

        Assert.True(BrowserDelegation.TryValidate(envelope, FullParent, out var problems), string.Join("; ", problems));
        Assert.Equal(BrowserDelegation.DefaultResultBudgetBytes, envelope.ResultBudgetBytes);
    }

    [Fact]
    public void AWriteCapableTaskAlsoRequiresNavigateAndInteract()
    {
        // 写入类任务必须真的拿到 navigate（§9 明写：注入列表须含任务实际需要的 navigate/screenshot）。
        var envelope = BrowserDelegationTasks.Create(
            goal: "填表并提交",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: true, CanManage: false),
            pageGrantId: "grant-2",
            expectedResult: "提交后的确认号",
            deadline: TimeSpan.FromMinutes(5));

        Assert.Contains(BrowserAgentToolIds.Navigate, envelope.RequiredTools);
        Assert.Contains(BrowserAgentToolIds.Interact, envelope.RequiredTools);
        Assert.Contains(BrowserAgentToolIds.Snapshot, envelope.RequiredTools);

        Assert.True(BrowserDelegation.TryValidate(envelope, FullParent, out var problems), string.Join("; ", problems));
    }

    [Fact]
    public void ExtraRequiredToolsAreHonoredAndDeduplicated()
    {
        var envelope = BrowserDelegationTasks.Create(
            goal: "等待新条目并读出来",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            pageGrantId: "grant-3",
            expectedResult: "新增条目标题",
            deadline: TimeSpan.FromMinutes(2),
            extraRequiredTools: [BrowserAgentToolIds.WaitFor, BrowserAgentToolIds.Snapshot]);

        Assert.Contains(BrowserAgentToolIds.WaitFor, envelope.RequiredTools);

        // 重复项只留一份（去重是工厂的职责，免得交给校验器当噪音）。
        Assert.Equal(
            envelope.RequiredTools.Count,
            envelope.RequiredTools.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TryCreateRefusesAScopeThatExceedsTheParent()
    {
        // 父只能读，子却要写 ⇒ 拒绝（子代理不得扩大父授权）。
        Assert.False(BrowserDelegationTasks.TryCreate(
            goal: "写入",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: true, CanManage: false),
            parentScope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            pageGrantId: "grant-4",
            expectedResult: "结果",
            deadline: TimeSpan.FromMinutes(1),
            out var envelope,
            out var problems));

        Assert.Null(envelope);
        Assert.Contains(problems, problem => problem.Contains("subset", StringComparison.Ordinal));
    }

    [Fact]
    public void TryCreateRefusesAnUnusableEnvelope()
    {
        // 缺页面 grant / 预算超上限：都由同一套校验拒绝（工厂不自己重写规则）。
        Assert.False(BrowserDelegationTasks.TryCreate(
            goal: "读",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            parentScope: FullParent,
            pageGrantId: "  ",
            expectedResult: "结果",
            deadline: TimeSpan.FromMinutes(1),
            out _,
            out var noGrant));
        Assert.Contains(noGrant, problem => problem.Contains("page grant", StringComparison.Ordinal));

        Assert.False(BrowserDelegationTasks.TryCreate(
            goal: "读",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            parentScope: FullParent,
            pageGrantId: "grant-5",
            expectedResult: "结果",
            deadline: TimeSpan.FromMinutes(1),
            out _,
            out var budget,
            resultBudgetBytes: BrowserDelegation.MaxResultBudgetBytes + 1));
        Assert.Contains(budget, problem => problem.Contains("must not exceed", StringComparison.Ordinal));

        // 正常情形能通过。
        Assert.True(BrowserDelegationTasks.TryCreate(
            goal: "读",
            delegatedScope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            parentScope: FullParent,
            pageGrantId: "grant-6",
            expectedResult: "结果",
            deadline: TimeSpan.FromMinutes(1),
            out var ok,
            out _));
        Assert.NotNull(ok);
    }

    [Fact]
    public void NullScopeIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => BrowserDelegationTasks.Create(
            "goal", null!, "grant", "expected", TimeSpan.FromMinutes(1)));
    }
}
