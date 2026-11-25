using PuddingCode.Abstractions;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>pool_action="list"</c> 的 SubSessionId 渲染。
/// <para>
/// <b>缺陷现场（2026-11-25 用工具自己撞出来的）：</b>条目的 <c>sub_session_id</c> 形如
/// <c>&lt;父会话 32 位&gt;-sub-&lt;8 位&gt;</c>，同一会话的五个成员共享那 32 位前缀。
/// 渲染成「前 8 位 + ...」之后整列五行完全一样：既分不出成员，也复制不出
/// <c>cleanup</c> 要用的 <c>sub_agent_id</c>（工具文档明写「可用 pool_action=list 查到」）。
/// </para>
/// <para>
/// 这组用例钉的是「信息完整」，不是「格式好看」：把截断改回去必须变红。
/// </para>
/// </summary>
[TestClass]
public sealed class PoolListingFormatterTests
{
    // 取现场真实前缀：五个成员都属于同一个父会话。
    private const string ParentSessionId = "3f52368a35ef49ab834991e897e8715f";

    [TestMethod]
    public void RenderSubSessionId_KeepsTheWholeValue()
    {
        var id = ParentSessionId + "-sub-67d3a424";

        // 正向对照：先证明「不截断」这件事本身成立，再谈副作用。
        Assert.AreEqual(id, PoolListingFormatter.RenderSubSessionId(id));
        Assert.IsTrue(PoolListingFormatter.RenderSubSessionId(id).EndsWith("-sub-67d3a424", StringComparison.Ordinal),
            "区分成员的正是 -sub- 之后的尾段，它必须原样保留");
    }

    [TestMethod]
    public void RenderSubSessionId_Blank_RendersDashButKeepsValidValue()
    {
        Assert.AreEqual("-", PoolListingFormatter.RenderSubSessionId(null));
        Assert.AreEqual("-", PoolListingFormatter.RenderSubSessionId("   "));

        // 负向不能靠「什么都返回 -」通过：有效值必须仍被原样输出。
        Assert.AreEqual("abc", PoolListingFormatter.RenderSubSessionId("abc"));
    }

    [TestMethod]
    public void Format_ContainsFullId_AndNoTruncatedForm()
    {
        var id = ParentSessionId + "-sub-67d3a424";

        var text = PoolListingFormatter.Format([Entry("scout", id)]);

        StringAssert.Contains(text, id,
            "list 必须给出完整 SubSessionId：cleanup 要求把 list 查到的值原样传给 sub_agent_id");

        // 旧缺陷形态：父会话前缀 + 省略号。它既是重复信息（同会话全一样），也无法复制使用。
        Assert.IsFalse(text.Contains(ParentSessionId[..8] + "...", StringComparison.Ordinal),
            $"仍在使用截断形态：{text}");
    }

    [TestMethod]
    public void Format_TwoMembersOfOneSession_HaveDifferentSubSessionCells()
    {
        var text = PoolListingFormatter.Format(
        [
            Entry("scout", ParentSessionId + "-sub-67d3a424"),
            Entry("reviewer", ParentSessionId + "-sub-88574a1a"),
        ]);

        var cells = RowCells(text)
            .Select(c => c[^2].Trim())   // 行末是 " |" ⟹ 末元素恒为空串，SubSessionId 在 [^2]
            .ToList();

        Assert.AreEqual(2, cells.Count, $"没解析出两行条目：{text}");
        Assert.IsFalse(string.IsNullOrEmpty(cells[0]), $"解析出的单元格为空，判据本身失效：{text}");
        Assert.AreNotEqual(cells[0], cells[1],
            "同会话两个成员的 SubSessionId 必须可区分，否则这一列只是装饰");
        StringAssert.Contains(cells[0], "-sub-67d3a424");
        StringAssert.Contains(cells[1], "-sub-88574a1a");
    }

    [TestMethod]
    public void Format_KeepsHeaderAndOtherColumns()
    {
        // 正向对照：改渲染不得顺手丢掉既有的列与值。
        var text = PoolListingFormatter.Format(
        [
            Entry("scout", ParentSessionId + "-sub-67d3a424", role: "verifier", taskCount: 7,
                status: PooledSubAgentStatus.Sleeping),
        ], static t => t);

        StringAssert.Contains(text, "| 名称 | 状态 | 角色 | 任务数 | 最后使用(本地) | SubSessionId |");
        var row = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("| scout |", StringComparison.Ordinal)));
        StringAssert.Contains(row, "Sleeping");
        StringAssert.Contains(row, "verifier");
        StringAssert.Contains(row, "| 7 |");
        // 日期必须跟着出现：只给 HH:mm:ss 时，「四天前」与「今天凌晨」在产物上不可区分。
        StringAssert.Contains(row, "2026-11-25 09:07");
    }

    [TestMethod]
    public void RenderLastUsed_UtcValue_RendersLocalDateAndClock()
    {
        // 现场真实值：frontend-ui 的 last_used_at 是 2026-11-21T04:26:17Z。
        var utc = new DateTimeOffset(2026, 11, 21, 4, 26, 17, TimeSpan.Zero);

        var rendered = PoolListingFormatter.RenderLastUsed(utc, t => t.ToOffset(TimeSpan.FromHours(8)));

        Assert.AreEqual("2026-11-21 12:26", rendered);
        // 旧缺陷形态：只有时分秒，而且那是 UTC 时刻。
        Assert.IsFalse(rendered.Contains("04:26", StringComparison.Ordinal), $"仍输出 UTC 时刻：{rendered}");
    }

    [TestMethod]
    public void RenderLastUsed_Default_RendersDash()
    {
        Assert.AreEqual("-", PoolListingFormatter.RenderLastUsed(default, static t => t));

        // 负向不能靠「恒返回 -」通过：有效值必须仍被渲染。
        Assert.AreEqual("2026-11-25 09:07",
            PoolListingFormatter.RenderLastUsed(
                new DateTimeOffset(2026, 11, 25, 9, 7, 5, TimeSpan.FromHours(8)), static t => t));
    }

    [TestMethod]
    public void Format_LastUsedColumn_UsesInjectedConverter()
    {
        // 端到端：证明 Format 真的把转换器传到了列渲染，而不是各算各的。
        var utc = new DateTimeOffset(2026, 11, 21, 4, 26, 17, TimeSpan.Zero);
        var text = PoolListingFormatter.Format(
            [Entry("frontend-ui", ParentSessionId + "-sub-e3680149", lastUsedAt: utc)],
            t => t.ToOffset(TimeSpan.FromHours(8)));

        var row = string.Join("\n", text.Split('\n')
            .Where(l => l.StartsWith("| frontend-ui |", StringComparison.Ordinal)));
        StringAssert.Contains(row, "| 2026-11-21 12:26 |");
        Assert.IsFalse(row.Contains("04:26:17", StringComparison.Ordinal), $"端到端仍输出旧形态：{row}");
    }

    // ===== 辅助 =====

    private static IEnumerable<string[]> RowCells(string table)
        => table.Split('\n')
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal)
                        && !l.StartsWith("| 名称", StringComparison.Ordinal)
                        && !l.StartsWith("|--", StringComparison.Ordinal))
            .Select(l => l.Split('|'));

    private static PooledSubAgent Entry(
        string name,
        string subSessionId,
        string? role = null,
        int taskCount = 0,
        PooledSubAgentStatus status = PooledSubAgentStatus.Idle,
        DateTimeOffset? lastUsedAt = null)
        => new()
        {
            PoolId = "poolid-" + name,
            Name = name,
            SubSessionId = subSessionId,
            TemplateId = "workspace-task-agent",
            Role = role,
            CreatedAt = DateTimeOffset.UnixEpoch,
            LastUsedAt = lastUsedAt ?? new DateTimeOffset(2026, 11, 25, 9, 7, 5, TimeSpan.FromHours(8)),
            Status = status,
            TaskCount = taskCount,
            OwnerSessionId = ParentSessionId,
            OwnerWorkspaceId = "default",
        };
}
