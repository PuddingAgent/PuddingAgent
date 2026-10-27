using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 前缀缓存安全回归（2026-10-27）：
///
/// DeepSeek 等 prefix-cache 接口按**最长公共前缀**命中缓存，命中价 ¥0.04/M、未命中 ¥2/M（差 50 倍）。
/// 因此「会话内既有工具的相对顺序不变、新工具只追加到末尾」不是风格问题，而是成本与延迟问题：
/// 一旦重排或从中间删除，该点之后的所有 token 都要重算。
///
/// 已有测试覆盖了发现（<see cref="ToolExposurePlanner.RegisterSearchResult"/>）、
/// 预算淘汰与能力包整包取舍，但**没有断言"稳定追加序"这个属性本身**。本文件补的就是它。
///
/// 说明：<see cref="ToolExposurePlanner"/> 是 internal，经 PuddingRuntime.csproj 的
/// InternalsVisibleTo("PuddingRuntimeTests") 可见。
/// </summary>
[TestClass]
public sealed class ToolExposureStableAppendTests
{
    private static readonly string[] ScrambledPrevious =
    [
        "search_tools",
        "goal_read",
        "deferred_05",
        "deferred_02",
    ];

    [TestMethod]
    public void AppendingNewTool_KeepsExistingRelativeOrder_AndPlacesNewItemAtTail()
    {
        var catalog = Catalog();

        // 第一轮：以"非 canonical"的既有顺序作为基线（模拟真实会话已被打乱过的历史顺序）。
        var first = ToolExposurePlanner.CreatePlan(catalog, previousVisibleToolIds: ScrambledPrevious);
        CollectionAssert.AreEqual(
            ScrambledPrevious,
            first.VisibleTools.Select(tool => tool.Name).ToArray(),
            "既有项必须原样保持上一轮的相对顺序");

        // 第二轮：只多加载一个工具。
        var second = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deferred_07" },
            previousVisibleToolIds: first.VisibleTools.Select(tool => tool.Name).ToArray());

        var secondNames = second.VisibleTools.Select(tool => tool.Name).ToArray();

        CollectionAssert.AreEqual(
            ScrambledPrevious,
            secondNames.Take(ScrambledPrevious.Length).ToArray(),
            "追加新工具不得改动任何既有项的位置——否则其后的 token 全部失去缓存");

        CollectionAssert.AreEqual(
            new[] { "deferred_07" },
            secondNames.Skip(ScrambledPrevious.Length).ToArray(),
            "新增项必须追加在末尾，且只有它一个");
    }

    [TestMethod]
    public void SameInputsTwice_ProducesIdenticalOrder_AndReportsNoSetChange()
    {
        var catalog = Catalog();
        var first = ToolExposurePlanner.CreatePlan(catalog, previousVisibleToolIds: ScrambledPrevious);
        var previous = first.VisibleTools.Select(tool => tool.Name).ToArray();

        // 第二轮什么都没新增、什么都没淘汰。
        var second = ToolExposurePlanner.CreatePlan(catalog, previousVisibleToolIds: previous);

        CollectionAssert.AreEqual(
            previous,
            second.VisibleTools.Select(tool => tool.Name).ToArray(),
            "无变化的一轮必须逐位相同（任何重排都会让缓存失效）");

        Assert.IsFalse(second.ExposureSetChanged, "集合没变就不应报告曝光集合变化");
        Assert.AreEqual(first.ExposureRevision, second.ExposureRevision,
            "无变化的一轮不得推进曝光 revision");
    }

    /// <summary>
    /// 特征化测试（characterization）：记录**当前**预算淘汰会从中间移除工具，
    /// 从而让其后所有 token 失去前缀缓存。
    ///
    /// 这是 §10.3 认定的唯一剩余 append-only 违反点。若将来实施「会话内不淘汰」（方案 B 最小版），
    /// 本测试会失败——**那是预期内的**，届时应改写本测试去断言"会话内可见集合只增不减"，
    /// 而不是把它当成回归改回去。
    /// </summary>
    [TestMethod]
    public void BudgetEviction_RemovesEarlierVisibleTool_WhileKeepingLaterOnes_WhichBreaksThePrefix()
    {
        var catalog = Catalog();
        var previous = new List<string> { "search_tools", "goal_read" };
        previous.AddRange(Enumerable.Range(0, 10).Select(index => $"deferred_{index:00}"));

        // 越靠后的 deferred 越"最近使用"，因此预算会优先砍掉最前面的 deferred_00..03。
        var recency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var index = 4; index < 10; index++)
            recency[$"deferred_{index:00}"] = 100 + index;

        var plan = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: previous.ToHashSet(StringComparer.OrdinalIgnoreCase),
            previousVisibleToolIds: previous,
            budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 8),
            toolUsageRecency: recency);

        Assert.IsTrue(plan.BudgetApplied);
        var visible = plan.VisibleTools.Select(tool => tool.Name).ToArray();

        CollectionAssert.DoesNotContain(visible, "deferred_00",
            "被淘汰的工具确实消失了");
        CollectionAssert.Contains(visible, "deferred_09",
            "但它后面的工具仍然保留 —— 这正是「中间被挖掉一个洞」的形态");

        // 断言的本质：可见列表不是上一轮的**前缀**，所以从洞的位置起，缓存全部失效。
        var isPrefixOfPrevious = visible.Length <= previous.Count
            && visible.SequenceEqual(previous.Take(visible.Length), StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(isPrefixOfPrevious,
            "当前实现会破坏前缀关系；若此断言失败说明已改成 append-only（方案 B），请按新语义改写本测试");
    }

    /// <summary>
    /// S2（2026-10-27）：开启 EvictOnlyAtSessionStart 后，会话内（已有上一轮可见集）不再淘汰。
    /// 这是把前缀从「可以变短」变成「只能变长」的那个开关。
    /// </summary>
    [TestMethod]
    public void EvictOnlyAtSessionStart_MidSession_DoesNotEvict_AndReportsDeferred()
    {
        var catalog = Catalog();
        var previous = VisibleSelection();

        var plan = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: previous.ToHashSet(StringComparer.OrdinalIgnoreCase),
            previousVisibleToolIds: previous,
            budget: new ToolExposurePlanner.ToolExposureBudget(
                MaxVisibleTools: 8, EvictOnlyAtSessionStart: true),
            toolUsageRecency: RecencyFavouringLaterTools());

        Assert.IsTrue(plan.BudgetDeferred, "会话内应报告「预算已推迟」而不是静默跳过");
        Assert.IsFalse(plan.BudgetApplied, "推迟即未应用淘汰");
        Assert.AreEqual(0, plan.EvictedToolCount, "会话内不得从可见集里删掉任何东西");
        Assert.AreEqual(previous.Count, plan.VisibleTools.Count,
            "可见集应保持上一轮的完整集合（此处 12 个 > 预算 8）");
        CollectionAssert.AreEquivalent(
            previous.ToArray(),
            plan.VisibleTools.Select(tool => tool.Name).ToArray(),
            "集合必须一致——少一个就意味着前缀被挖了洞");
        Assert.IsTrue(plan.VisibleToolBytes > 0);
    }

    /// <summary>会话首个计划（没有上一轮可见集）仍应正常淘汰，把预算在会话边界上执行到底。</summary>
    [TestMethod]
    public void EvictOnlyAtSessionStart_FirstPlanOfSession_StillEvicts()
    {
        var catalog = Catalog();

        var plan = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: VisibleSelection().ToHashSet(StringComparer.OrdinalIgnoreCase),
            previousVisibleToolIds: null,
            budget: new ToolExposurePlanner.ToolExposureBudget(
                MaxVisibleTools: 8, EvictOnlyAtSessionStart: true),
            toolUsageRecency: RecencyFavouringLaterTools());

        Assert.IsTrue(plan.BudgetApplied, "会话起点必须真正执行预算");
        Assert.IsFalse(plan.BudgetDeferred);
        Assert.AreEqual(8, plan.VisibleTools.Count);
        Assert.IsTrue(plan.EvictedToolCount > 0);
    }

    /// <summary>未开启开关时，行为必须与改动前逐字一致（默认值不变是回滚的前提）。</summary>
    [TestMethod]
    public void WithoutFlag_MidSession_StillEvicts_AndNeverReportsDeferred()
    {
        var catalog = Catalog();
        var previous = VisibleSelection();

        var plan = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: previous.ToHashSet(StringComparer.OrdinalIgnoreCase),
            previousVisibleToolIds: previous,
            budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 8),
            toolUsageRecency: RecencyFavouringLaterTools());

        Assert.IsTrue(plan.BudgetApplied);
        Assert.IsFalse(plan.BudgetDeferred, "未开开关时不得报告推迟");
        Assert.AreEqual(8, plan.VisibleTools.Count);
        Assert.IsTrue(plan.EvictedToolCount > 0);
    }

    private static List<string> VisibleSelection()
    {
        var names = new List<string> { "search_tools", "goal_read" };
        names.AddRange(Enumerable.Range(0, 10).Select(index => $"deferred_{index:00}"));
        return names;
    }

    /// <summary>越靠后的工具越“最近使用”，因此不推迟时预算会优先砍掉前面的。</summary>
    private static Dictionary<string, long> RecencyFavouringLaterTools()
    {
        var recency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var index = 4; index < 10; index++)
            recency[$"deferred_{index:00}"] = 100 + index;
        return recency;
    }

    private static List<LlmToolDefinition> Catalog()
    {
        // deferred_loading 阈值是 24，故必须给足工具数，否则预算路径不会被启用。
        var tools = new List<LlmToolDefinition>
        {
            Definition("search_tools"),
            Definition("goal_read"),
        };
        tools.AddRange(Enumerable.Range(0, 30).Select(index => Definition($"deferred_{index:00}")));
        return tools;
    }

    private static LlmToolDefinition Definition(string name) => new()
    {
        Name = name,
        Description = name,
        Parameters = new ToolParameterSchema([], []),
    };
}
