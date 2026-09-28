using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-17 orchestration slice: a draft needs a graph id and a JSON object, publishing is CAS, and the hook card
/// states that triggers are configured by publishing a revision and that configuration values are not echoed.
/// </summary>
public sealed class OrchestrationContractTests
{
    private static OrchestrationRevisionDraft Draft() => new("graph-1", 3, """{"graphId":"graph-1"}""");

    [Fact]
    public void DraftsNeedAGraphIdAndAJsonObject()
    {
        Assert.Empty(OrchestrationText.Validate(Draft()));
        Assert.Contains("GraphId", OrchestrationText.Validate(Draft() with { GraphId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("不能为空", OrchestrationText.Validate(Draft() with { DefinitionJson = "  " }).Single(), StringComparison.Ordinal);
        // 数组或裸值不是图定义。
        Assert.Contains("JSON 对象", OrchestrationText.Validate(Draft() with { DefinitionJson = "[1,2]" }).Single(), StringComparison.Ordinal);
        Assert.Contains("JSON 对象", OrchestrationText.Validate(Draft() with { DefinitionJson = "42" }).Single(), StringComparison.Ordinal);
        Assert.True(OrchestrationText.LooksLikeJsonObject(" { } "));
        Assert.False(OrchestrationText.LooksLikeJsonObject("{"));
        // 新建图用 0，负数无效。
        Assert.Contains("修订号", OrchestrationText.Validate(Draft() with { ExpectedCurrentRevision = -1 }).Single(), StringComparison.Ordinal);
        Assert.Empty(OrchestrationText.Validate(Draft() with { ExpectedCurrentRevision = 0 }));
    }

    [Fact]
    public void ManualRunNeedsBothGraphAndRevision()
    {
        Assert.Empty(OrchestrationText.Validate("graph-1", "rev-1"));
        Assert.Contains("图与修订", OrchestrationText.Validate("", "rev-1").Single(), StringComparison.Ordinal);
        Assert.Contains("图与修订", OrchestrationText.Validate("graph-1", "").Single(), StringComparison.Ordinal);
        Assert.Contains("RevisionId", OrchestrationText.HookNotice.Length > 0 ? "RevisionId" : "", StringComparison.Ordinal);
    }

    [Fact]
    public void GraphAndRevisionTextUseCoreFieldsOnly()
    {
        var graph = new OrchestrationGraphSummary("graph-1", "default", "s-1", "agent-1", "Ship the thing",
            4, "rev-4", 7, 2, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        Assert.Contains("修订 4", graph.LineText, StringComparison.Ordinal);
        Assert.Contains("运行 7", graph.LineText, StringComparison.Ordinal);
        Assert.Contains("进行中 2", graph.LineText, StringComparison.Ordinal);

        var revision = new OrchestrationRevisionSummary("graph-1", "rev-4", 4, "rev-3", "v2",
            "abcdef0123456789", "agent-1", DateTimeOffset.UtcNow);
        Assert.Contains("修订 4", revision.LineText, StringComparison.Ordinal);
        // 哈希只显示前 12 位，便于人读又不至于刷屏。
        Assert.Contains("abcdef012345", revision.LineText, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef0123456789", revision.LineText, StringComparison.Ordinal);

        var detail = new OrchestrationGraphDetail("graph-1", "rev-4", 4, "default", "objective", "v2", "",
            3, true,
            [new OrchestrationNodeSummary("n-1", "agent.task", "1.0", "Plan")],
            [new OrchestrationEdgeSummary("n-1", "n-2", "OnSuccess")],
            [new OrchestrationTriggerSummary("hook-1", "pudding.trigger.http", "1.0", true, ["secretRef"], ["$ → input-1"])],
            [new OrchestrationGraphInput("input-1", "text", true)]);
        Assert.Contains("节点 1", detail.HeadlineText, StringComparison.Ordinal);
        Assert.Contains("触发器 1", detail.HeadlineText, StringComparison.Ordinal);
        Assert.Equal(1, detail.EnabledTriggerCount);
        // 定义没有内容哈希时明确说明去哪儿看。
        Assert.Contains("哈希见修订列表", detail.MetaText, StringComparison.Ordinal);
        Assert.Contains("需要显式激活", detail.MetaText, StringComparison.Ordinal);
        Assert.Contains("（agent.task@1.0）", detail.Nodes[0].LineText, StringComparison.Ordinal);
        Assert.Contains("端口 OnSuccess", detail.Edges[0].LineText, StringComparison.Ordinal);
        Assert.Contains("必填", detail.Inputs[0].LineText, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerTextShowsStateBindingsAndConfigurationKeysButNoValues()
    {
        var trigger = new OrchestrationTriggerSummary("hook-1", "pudding.trigger.http", "1.0", false,
            ["AuthorizationRef", "Path"], ["$ → input-1", "$.body → input-2"]);
        Assert.Equal("已停用", trigger.StateText);
        Assert.Contains("输入映射 2 项", trigger.LineText, StringComparison.Ordinal);
        // 只出现键名——配置值（可能引用凭据）绝不回显。
        Assert.Contains("AuthorizationRef", trigger.LineText, StringComparison.Ordinal);
        Assert.DoesNotContain("=", trigger.LineText, StringComparison.Ordinal);
        Assert.Contains("hook-1", trigger.InvocationPath("graph-1"), StringComparison.Ordinal);
        Assert.Equal("/api/orchestrations/hooks/graph-1/hook-1", trigger.InvocationPath("graph-1"));

        var noConfig = trigger with { ConfigurationKeys = [], InputBindings = [] };
        Assert.Contains("配置键 无", noConfig.LineText, StringComparison.Ordinal);
        Assert.True((trigger with { Enabled = true }).StateText == "已启用");
    }

    [Fact]
    public void ValidationResultAndNoticesAreExplicit()
    {
        var ok = new OrchestrationValidationResult(true, [], ["n-1", "n-2"]);
        Assert.Contains("校验通过", ok.DescribeText, StringComparison.Ordinal);
        Assert.Contains("拓扑顺序 2 个节点", ok.DescribeText, StringComparison.Ordinal);
        Assert.Equal("没有问题", ok.IssuesText);

        var bad = new OrchestrationValidationResult(false, ["orchestration.edge_cycle: cycle detected"], []);
        Assert.Contains("校验失败", bad.DescribeText, StringComparison.Ordinal);
        Assert.Contains("orchestration.edge_cycle", bad.IssuesText, StringComparison.Ordinal);

        // 三条边界必须写在卡片上。
        Assert.Contains("独立原生工作页", OrchestrationText.ScopeNotice, StringComparison.Ordinal);
        Assert.Contains("没有独立的增删改/启停接口", OrchestrationText.HookNotice, StringComparison.Ordinal);
        Assert.Contains("发布一个新修订", OrchestrationText.HookNotice, StringComparison.Ordinal);
        Assert.Contains("只显示**配置键名**", OrchestrationText.HookSecretNotice, StringComparison.Ordinal);
        Assert.Contains("ExpectedCurrentRevision", OrchestrationText.CasNotice, StringComparison.Ordinal);
        Assert.Contains("不落盘", OrchestrationText.ValidateNotice, StringComparison.Ordinal);
        Assert.Contains("启动新的运行", OrchestrationText.TriggerSemanticsNotice, StringComparison.Ordinal);
        Assert.Equal(200, OrchestrationText.ClampLimit(10_000));
        Assert.Equal(1, OrchestrationText.ClampLimit(0));
        Assert.Equal("待激活", OrchestrationText.DescribeRunState("pending"));
        Assert.Equal("状态未知", OrchestrationText.DescribeRunState(null));
    }
}
