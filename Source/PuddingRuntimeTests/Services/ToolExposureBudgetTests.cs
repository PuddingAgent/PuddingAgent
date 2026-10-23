using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 工具曝光预算（有界可见集）回归：
/// 长会话不能把全部已发现工具 schema 当固定前缀每轮发送；预算内按「最近使用 → 上一轮可见顺序」
/// 保留非核心工具，核心工具永不淘汰，无 search_tools 恢复路径时不启用淘汰。
/// </summary>
[TestClass]
public sealed class ToolExposureBudgetTests
{
    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        ToolExposurePlanner.SearchToolId,
        "goal_read",
    };

    [TestMethod]
    public void Budget_EvictsLeastRecentlyUsedNonCoreTools_AndKeepsCore()
    {
        var catalog = Catalog();
        // 上一轮可见 12 个：core + deferred_00..09；deferred_20..22 最近刚用过。
        var previous = new List<string> { "search_tools", "goal_read" };
        previous.AddRange(Enumerable.Range(0, 10).Select(i => $"deferred_{i:00}"));
        var recency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["deferred_20"] = 100,
            ["deferred_21"] = 101,
            ["deferred_22"] = 102,
        };

        var plan = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: catalog.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            previousVisibleToolIds: previous,
            budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 6),
            toolUsageRecency: recency);

        Assert.IsTrue(plan.BudgetApplied);
        Assert.AreEqual(6, plan.VisibleTools.Count, "预算上限必须扣住 total visible 数量");
        var visible = plan.VisibleTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CollectionAssert.IsSubsetOf(Core.ToArray(), visible.ToArray(), "核心工具永不淘汰");
        Assert.IsTrue(visible.Contains("deferred_22") && visible.Contains("deferred_21") && visible.Contains("deferred_20"),
            "最近使用的工具必须优先保留");
        Assert.IsFalse(visible.Contains("deferred_05"), "最久未使用的旧可见工具应被淘汰");
        Assert.AreEqual(catalog.Count - 6, plan.EvictedToolCount);
        CollectionAssert.DoesNotContain(plan.EvictedToolIds!.ToArray(), "search_tools");
        Assert.IsTrue(plan.VisibleToolBytes > 0);
        Assert.IsTrue(plan.ExposureSetChanged, "有界淘汰构成一次可归因的曝光集合变化");
    }

    [TestMethod]
    public void Budget_ByteAxis_TruncatesLargeSchemas()
    {
        var catalog = Catalog(descriptionLength: 400);
        var loaded = catalog.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var budget = new ToolExposurePlanner.ToolExposureBudget(MaxVisibleToolBytes: 1_500);
        var plan = ToolExposurePlanner.CreatePlan(catalog, loaded, budget: budget);

        Assert.IsTrue(plan.BudgetApplied);
        Assert.IsTrue(plan.EvictedToolCount > 0, "字节预算必须截断部分 schema");
        Assert.IsTrue(plan.VisibleToolBytes < 1_500 + 2 * 600,
            $"可见集不应明显越过字节预算（actual={plan.VisibleToolBytes}）");
        var visible = plan.VisibleTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CollectionAssert.IsSubsetOf(Core.ToArray(), visible.ToArray());
        Assert.IsTrue(plan.VisibleTools.Count < catalog.Count);
    }

    [TestMethod]
    public void Budget_NotApplied_WhenNoSearchToolsRecoveryPath()
    {
        var catalog = Catalog(extraDeferred: 30).Where(tool => tool.Name != ToolExposurePlanner.SearchToolId).ToList();
        var plan = ToolExposurePlanner.CreatePlan(
            catalog,
            budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 5));

        Assert.IsFalse(plan.BudgetApplied, "无 search_tools 时淘汰会让工具永久不可达");
        Assert.AreEqual(catalog.Count, plan.VisibleTools.Count);
        Assert.AreEqual(0, plan.EvictedToolCount);
    }

    [TestMethod]
    public void Budget_CapabilityBundle_IsAllOrNothing()
    {
        var catalog = Catalog(extraDeferred: 22, includeTerminalFamily: true);
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "terminal_start" };
        var recency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["terminal_start"] = 1_000,
        };
        var terminalFamily = new[]
        {
            "shell", "terminal_start", "terminal_read", "terminal_wait", "terminal_input", "terminal_cancel",
        };

        // core(2) + 6 个 terminal 工具 > 5 → 整包跳过，不得只放半个 terminal 族。
        var tooSmall = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: loaded,
            budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 5),
            toolUsageRecency: recency);
        var tooSmallVisible = tooSmall.VisibleTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(tooSmallVisible.Overlaps(terminalFamily), "能力包不得被切半");

        // core(2) + 6 = 8 → 整包放行。
        var fits = ToolExposurePlanner.CreatePlan(
            catalog,
            loadedToolIds: loaded,
            budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 8),
            toolUsageRecency: recency);
        var fitsVisible = fits.VisibleTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CollectionAssert.IsSubsetOf(terminalFamily, fitsVisible.ToArray(), "预算足够时必须整包保留");
    }

    [TestMethod]
    public void DispatchManifest_AppliesBudget_AndReportsVisibleBytes()
    {
        var catalog = Catalog(extraDeferred: 40);
        var loaded = catalog.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = ToolExposurePlanner.CreatePlan(catalog, loaded, budget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 12));
        var manifest = AgentExecutionService.BuildFrozenToolManifestCore(
            catalog,
            null,
            loaded,
            toolUsageRecency: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            exposureBudget: new ToolExposurePlanner.ToolExposureBudget(MaxVisibleTools: 12));

        Assert.IsTrue(manifest.ExposurePlan.BudgetApplied);
        Assert.AreEqual(plan.VisibleTools.Count, manifest.VisibleTools.Count);
        Assert.IsTrue(manifest.VisibleTools.Count <= 12);
        Assert.IsTrue(manifest.ExposurePlan.EvictedToolCount > 0);
    }

    private static List<LlmToolDefinition> Catalog(
        int extraDeferred = 24,
        int descriptionLength = 40,
        bool includeTerminalFamily = false)
    {
        var tools = new List<LlmToolDefinition>
        {
            Definition("search_tools", descriptionLength),
            Definition("goal_read", descriptionLength),
        };

        if (includeTerminalFamily)
        {
            foreach (var name in new[]
                     {
                         "shell", "terminal_start", "terminal_read", "terminal_wait", "terminal_input", "terminal_cancel",
                     })
                tools.Add(Definition(name, descriptionLength));
        }

        for (var i = 0; i < extraDeferred; i++)
            tools.Add(Definition($"deferred_{i:00}", descriptionLength));

        return tools;
    }

    private static LlmToolDefinition Definition(string name, int descriptionLength) => new()
    {
        Name = name,
        Description = new string('d', descriptionLength),
        Parameters = new ToolParameterSchema([], []),
    };
}
