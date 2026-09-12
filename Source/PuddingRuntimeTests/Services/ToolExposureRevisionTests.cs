using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-B-2 行为层纵切回归：工具曝光**稳定追加序** + <c>ExposureRevision</c> 与 <c>PermissionEpoch</c> 分离
/// + 轮边界提交事实 + 缺失/已撤销工具定义 fail-closed（不谎称精确恢复、不阻塞执行）。
/// 覆盖验收 C01B-AC4 / AC6 / AC8 中「工具按需发现、撤销权限、旧工具定义缺失、纯温热复用」条目。
/// </summary>
[TestClass]
public sealed class ToolExposureRevisionTests
{
    /// <summary>
    /// 稳定追加序实断言：新增工具后既有工具相对顺序**不变**，新增项只追加到末尾；
    /// 相对 legacy 全量字母序的偏差必须是一次显式 epoch（且只声明一次）。
    /// </summary>
    [TestMethod]
    public void Plan_AppendsNewToolOnly_AndKeepsExistingRelativeOrder()
    {
        var tools = Catalog(extraDeferred: 24); // 26 个工具 > 阈值 24 → 启用延迟加载
        var initial = ToolExposurePlanner.CreatePlan(tools);
        Assert.IsTrue(initial.DeferredLoadingEnabled);
        var initialIds = initial.VisibleTools.Select(tool => tool.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "goal_read", "search_tools" }, initialIds);
        Assert.AreEqual(0, initial.ExposureRevision, "会话首个计划不构成曝光集合变化");
        Assert.IsFalse(initial.OrderingStrategyDeclared);

        var appended = ToolExposurePlanner.CreatePlan(
            tools,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deferred_20" },
            previousVisibleToolIds: initialIds,
            epoch: new ToolExposureEpoch(initial.ExposureRevision, initial.OrderingStrategyDeclared));

        CollectionAssert.AreEqual(
            new[] { "goal_read", "search_tools", "deferred_20" },
            appended.VisibleTools.Select(tool => tool.Name).ToArray(),
            "稳定追加序：既有项相对顺序不变，新增项只追加到末尾（不得对全量集合重新字母排序）");
        Assert.AreEqual(ToolExposurePlanner.OrderingStrategyStableAppend, appended.OrderingStrategy);
        Assert.AreEqual(1, appended.ExposureRevision, "曝光集合变化 → ExposureRevision +1");
        Assert.IsTrue(appended.ExposureSetChanged);
        StringAssert.Contains(appended.ChangeReason, CompositionChangeReasons.ExposureChanged);
        StringAssert.Contains(appended.ChangeReason, CompositionChangeReasons.OrderingStrategyChanged);
        Assert.IsTrue(appended.OrderingStrategyChanged, "相对 legacy 字母序的偏差必须是一次显式 epoch");
        Assert.IsTrue(appended.OrderingStrategyDeclared);
        Assert.IsTrue(appended.ExactRestore, "既有定义全部仍存在 → 可精确重建");
        Assert.AreEqual(0, appended.MissingToolIds?.Count ?? 0);

        var second = ToolExposurePlanner.CreatePlan(
            tools,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deferred_20", "deferred_21" },
            previousVisibleToolIds: appended.VisibleTools.Select(tool => tool.Name).ToArray(),
            epoch: new ToolExposureEpoch(appended.ExposureRevision, appended.OrderingStrategyDeclared));

        CollectionAssert.AreEqual(
            new[] { "goal_read", "search_tools", "deferred_20", "deferred_21" },
            second.VisibleTools.Select(tool => tool.Name).ToArray());
        Assert.AreEqual(2, second.ExposureRevision);
        Assert.IsFalse(second.OrderingStrategyChanged, "排序策略 epoch 只允许声明一次");
        Assert.IsFalse(second.ChangeReason.Contains(CompositionChangeReasons.OrderingStrategyChanged));
    }

    /// <summary>纯温热复用（曝光集合未变）不得推进 ExposureRevision，也不得上报任何原因。</summary>
    [TestMethod]
    public void Plan_WarmReuse_DoesNotAdvanceExposureRevision()
    {
        var tools = Catalog(extraDeferred: 24);
        var previousIds = new[] { "goal_read", "search_tools", "deferred_20" };

        var warm = ToolExposurePlanner.CreatePlan(
            tools,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deferred_20" },
            previousVisibleToolIds: previousIds,
            epoch: new ToolExposureEpoch(7, OrderingStrategyDeclared: true));

        Assert.AreEqual(7, warm.ExposureRevision, "集合未变 → 不得推进 ExposureRevision");
        Assert.IsFalse(warm.ExposureSetChanged);
        Assert.AreEqual(CompositionChangeReasons.None, warm.ChangeReason);
        CollectionAssert.AreEqual(previousIds, warm.VisibleTools.Select(tool => tool.Name).ToArray());
    }

    /// <summary>
    /// 旧工具定义缺失 / 已撤销：不谎称精确恢复（ExactRestore=false + tool_definition_missing），
    /// 但**不得阻塞执行**，且缺失工具不得被“恢复”为可见（R5/R7）。
    /// </summary>
    [TestMethod]
    public void Plan_MissingToolDefinition_ReportsMissingWithoutBlocking()
    {
        var tools = Catalog(extraDeferred: 24);
        var previousIds = new[] { "goal_read", "search_tools", "deferred_20", "removed_tool" };

        var plan = ToolExposurePlanner.CreatePlan(
            tools,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deferred_20" },
            previousVisibleToolIds: previousIds,
            epoch: new ToolExposureEpoch(3, OrderingStrategyDeclared: true));

        CollectionAssert.Contains(plan.MissingToolIds!.ToArray(), "removed_tool");
        Assert.IsFalse(plan.ExactRestore, "缺失定义时不得谎称精确恢复");
        StringAssert.Contains(plan.ChangeReason, CompositionChangeReasons.ToolDefinitionMissing);
        var visibleIds = plan.VisibleTools.Select(tool => tool.Name).ToArray();
        CollectionAssert.DoesNotContain(visibleIds, "removed_tool", "已撤销/缺失工具不得被恢复为可见");
        CollectionAssert.Contains(visibleIds, "goal_read", "不得阻塞执行：其余可用工具照常可见");
        Assert.AreEqual(4, plan.ExposureRevision, "缺失定义同样构成曝光集合变化");
    }

    /// <summary>工具按需发现只推进曝光纪元，绝不推进权限纪元（AC4 分原因）。</summary>
    [TestMethod]
    public void Registry_ExposureChange_DoesNotIncrementPermissionEpoch()
    {
        var registry = new CompositionVersionRegistry();
        registry.Observe("s-exposure", "sys", "tool-a", toolIds: new[] { "goal_read", "search_tools" });

        var observed = registry.Observe(
            "s-exposure",
            "sys",
            "tool-b",
            toolIds: new[] { "goal_read", "search_tools", "file_read" });

        Assert.AreEqual(0, observed.PermissionEpoch, "工具发现不得触碰 PermissionEpoch");
        Assert.AreEqual(1, observed.ExposureRevision);
        StringAssert.Contains(observed.ChangeReason, CompositionChangeReasons.ExposureChanged);
        Assert.IsFalse(observed.ChangeReason.Contains(CompositionChangeReasons.PermissionChanged));
    }

    /// <summary>权限撤销只推进权限纪元，不得被记为工具发现（AC4 分原因，R5 权限边界不变）。</summary>
    [TestMethod]
    public void Registry_PermissionChange_DoesNotIncrementExposureRevision()
    {
        var registry = new CompositionVersionRegistry();
        registry.Observe(
            "s-permission",
            "sys",
            "tool",
            toolIds: new[] { "a", "b" },
            permissionFingerprint: "fp-1");

        var observed = registry.Observe(
            "s-permission",
            "sys",
            "tool",
            toolIds: new[] { "a", "b" },
            permissionFingerprint: "fp-2");

        Assert.AreEqual(1, observed.PermissionEpoch);
        Assert.AreEqual(0, observed.ExposureRevision, "权限变化不得被记为工具发现");
        StringAssert.Contains(observed.ChangeReason, CompositionChangeReasons.PermissionChanged);
        Assert.IsFalse(observed.ChangeReason.Contains(CompositionChangeReasons.ExposureChanged));
    }

    private static List<LlmToolDefinition> Catalog(int extraDeferred)
    {
        var tools = new List<LlmToolDefinition>
        {
            Definition("search_tools"),
            Definition("goal_read"),
        };
        for (var i = 0; i < extraDeferred; i++)
            tools.Add(Definition($"deferred_{i:00}"));
        return tools;
    }

    private static LlmToolDefinition Definition(string name) => new()
    {
        Name = name,
        Description = $"Description for {name}",
        Parameters = new ToolParameterSchema([], []),
    };
}
