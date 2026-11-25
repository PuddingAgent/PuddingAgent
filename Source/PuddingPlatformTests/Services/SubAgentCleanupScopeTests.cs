using PuddingCode.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 子代理查询 / 清理过滤器的语义（<see cref="SubAgentQueryFiltering"/>）。
/// <para>
/// 为什么单独锁这层：<c>SubAgentManager.GetSubAgentsAsync</c> 此前收了
/// <see cref="SubAgentQueryFilter"/> 却完全忽略，于是 <c>CleanupAsync</c> 传下去的
/// <c>Status</c> 与 <c>MaxResults</c> 一起失效。<c>pool_action=cleanup</c> 以为自己
/// 只清一个成员的一条记录（<c>MaxCount=1</c>），实际候选集是该父会话下所有非运行中的
/// 子代理——清掉谁取决于返回顺序，可能是另一个成员的历史，也可能恰好清 0 条。
/// </para>
/// <para>
/// 参数被静默忽略必须由用例兜住：它不报错、不抛异常，只会让约束看起来生效了。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentCleanupScopeTests
{
    // ═══════════════════════════════════════════════════════
    // 1. 查询过滤器真的生效
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public void Apply_HonorsStatusTemplateAndMaxResults()
    {
        var all = new[]
        {
            Status("sub-a", "failed", "workspace-task-agent"),
            Status("sub-b", "completed", "workspace-task-agent"),
            Status("sub-c", "failed", "reviewer-agent"),
        };

        var byStatus = SubAgentQueryFiltering.Apply(all, new SubAgentQueryFilter { Status = "failed" });
        CollectionAssert.AreEqual(new[] { "sub-a", "sub-c" }, Ids(byStatus));

        var byTemplate = SubAgentQueryFiltering.Apply(all, new SubAgentQueryFilter { TemplateId = "reviewer-agent" });
        CollectionAssert.AreEqual(new[] { "sub-c" }, Ids(byTemplate));

        var capped = SubAgentQueryFiltering.Apply(all, new SubAgentQueryFilter { MaxResults = 2 });
        CollectionAssert.AreEqual(new[] { "sub-a", "sub-b" }, Ids(capped),
            "MaxResults 必须真的截断：忽略它时调用方会拿到整份列表");
    }

    [TestMethod]
    public void Apply_TreatsAllAndNullAsNoStatusFilter()
    {
        var all = new[]
        {
            Status("sub-a", "failed"),
            Status("sub-b", "completed"),
        };

        // 正向对照：同一份输入配「有过滤」必须变少，否则「全部都返回」也可能是
        // 过滤器把一切都放行（那是另一种失效，用例得能区分）。
        CollectionAssert.AreEqual(new[] { "sub-a" }, Ids(
            SubAgentQueryFiltering.Apply(all, new SubAgentQueryFilter { Status = "failed" })));

        // "all" 是 SubAgentTool 的默认值：若把它当字面状态比较，默认行为会从
        // 「全部」变成「零条」。
        CollectionAssert.AreEqual(new[] { "sub-a", "sub-b" }, Ids(
            SubAgentQueryFiltering.Apply(all, new SubAgentQueryFilter { Status = "all" })));

        CollectionAssert.AreEqual(new[] { "sub-a", "sub-b" }, Ids(
            SubAgentQueryFiltering.Apply(all, new SubAgentQueryFilter())));
    }

    // ═══════════════════════════════════════════════════════
    // 2. 清理只动指定成员
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public void SelectForCleanup_TargetsOnlyNamedSubSession()
    {
        var all = new[]
        {
            Status("sub-a", "completed"),
            Status("sub-b", "completed"),
        };

        var onlyA = SubAgentQueryFiltering.SelectForCleanup(all, new SubAgentCleanupFilter
        {
            SubSessionId = "sub-a",
            MaxCount = 1,
        });
        CollectionAssert.AreEqual(new[] { "sub-a" }, Ids(onlyA), "清理 A 不得碰到 B");

        var onlyB = SubAgentQueryFiltering.SelectForCleanup(all, new SubAgentCleanupFilter
        {
            SubSessionId = "sub-b",
            MaxCount = 1,
        });
        CollectionAssert.AreEqual(new[] { "sub-b" }, Ids(onlyB), "两个成员的归档互不受影响");
    }

    [TestMethod]
    public void SelectForCleanup_AppliesMaxCountAfterFiltering()
    {
        // 第一条不匹配状态筛选。若先 Take(1) 再筛，得到的是空的 ——
        // 那正是「最多清 1 条」变成「清 0 条」的成因。
        var all = new[]
        {
            Status("sub-completed", "completed"),
            Status("sub-failed", "failed"),
        };

        var selected = SubAgentQueryFiltering.SelectForCleanup(all, new SubAgentCleanupFilter
        {
            Status = "failed",
            MaxCount = 1,
        });

        CollectionAssert.AreEqual(new[] { "sub-failed" }, Ids(selected),
            "截断必须在筛选之后，否则目标条目会被先截掉");
    }

    [TestMethod]
    public void SelectForCleanup_SkipsRunningEntries()
    {
        var all = new[]
        {
            Status("sub-running", "running"),
            Status("sub-done", "completed"),
        };

        var selected = SubAgentQueryFiltering.SelectForCleanup(all, new SubAgentCleanupFilter());
        CollectionAssert.AreEqual(new[] { "sub-done" }, Ids(selected),
            "运行中的记录是它收尾时唯一的落点，不得被清理");
    }

    [TestMethod]
    public void SelectForCleanup_TakesOldestFirst_EvenWhenInputIsNewestFirst()
    {
        // 入参形状取真实调用链：GetSubAgentsAsync 按 SpawnedAt 降序返回。
        var all = new[]
        {
            StatusAt("sub-newest", 1, "completed"),
            StatusAt("sub-middle", 5, "completed"),
            StatusAt("sub-oldest", 20, "completed"),
        };

        var two = SubAgentQueryFiltering.SelectForCleanup(all, new SubAgentCleanupFilter { MaxCount = 2 });
        CollectionAssert.AreEqual(new[] { "sub-oldest", "sub-middle" }, Ids(two),
            "bulk 清理必须删最旧的：直接 Take 会挑走最新两条，与保留策略正好相反");

        // 正向对照：不截断时三条都在，证明上面少的不是「筛掉了」。
        var all3 = SubAgentQueryFiltering.SelectForCleanup(all, new SubAgentCleanupFilter { MaxCount = 3 });
        CollectionAssert.AreEqual(new[] { "sub-oldest", "sub-middle", "sub-newest" }, Ids(all3));
    }

    // ── 脚手架 ──────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    private static SubAgentStatus Status(string subSessionId, string status, string? templateId = "workspace-task-agent")
        => new()
        {
            RunId = "run-" + subSessionId,
            ParentSessionId = "parent-session",
            SubSessionId = subSessionId,
            Status = status,
            TemplateId = templateId,
            TaskSummary = "task of " + subSessionId,
            SpawnedAt = Now.AddHours(-1),
        };

    private static SubAgentStatus StatusAt(string subSessionId, int hoursAgo, string status)
        => new()
        {
            RunId = "run-" + subSessionId,
            ParentSessionId = "parent-session",
            SubSessionId = subSessionId,
            Status = status,
            TemplateId = "workspace-task-agent",
            TaskSummary = "task of " + subSessionId,
            SpawnedAt = Now.AddHours(-hoursAgo),
        };

    private static string[] Ids(IReadOnlyList<SubAgentStatus> list)
        => list.Select(s => s.SubSessionId).ToArray();
}
