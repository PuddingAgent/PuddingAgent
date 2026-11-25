using PuddingCode.Abstractions;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>pool_action="cleanup-bulk"</c> 预演回执的「候选构成」。
/// <para>
/// <b>缺陷现场：</b>批量清理的候选范围是<b>整个父会话</b>（不限池化成员），
/// 而回执原先只说「命中 N 条」+ 列 10 条明细。读的人因此无法回答那个真正的问题：
/// <b>删掉会不会动到我还在用的成员</b>。这不是格式问题 —— 它决定了要不要按下删除键，
/// 而删除的是运行归档（含磁盘目录），不可恢复。
/// </para>
/// <para>
/// 第二组用例钉的是「不知道 ≠ 不是你的」：池未就绪时把构成说成「全部是历史记录」，
/// 会让人误以为可以放心删。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentToolBulkCompositionTests
{
    private const string ParentSessionId = "3f52368a35ef49ab834991e897e8715f";

    [TestMethod]
    public void Composition_SplitsOwnedAndHistorical()
    {
        var candidates = new[]
        {
            Candidate("sub-a"),
            Candidate("sub-b"),
            Candidate("sub-orphan"),
        };
        var members = new[]
        {
            Member("scout", "sub-a"),
            Member("reviewer", "sub-b"),
        };

        var text = SubAgentTool.DescribeBulkCandidateComposition(candidates, members);

        StringAssert.Contains(text, "其中 2 条属于当前池化成员");
        StringAssert.Contains(text, "scout");
        StringAssert.Contains(text, "reviewer");
        StringAssert.Contains(text, "另有 1 条是已不在池中的历史记录");
        // 删归档不等于销毁成员，这句必须说清，否则用户会以为删了就得重建。
        StringAssert.Contains(text, "成员本身仍留在池中");
        // 正向对照：全归一类时才会出现的措辞不得出现。
        Assert.IsFalse(text.Contains("全部 3 条都属于", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Composition_AllOwned_SaysAllAndExplainsMembersSurvive()
    {
        var candidates = new[] { Candidate("sub-a"), Candidate("sub-b") };
        var members = new[] { Member("scout", "sub-a"), Member("reviewer", "sub-b") };

        var text = SubAgentTool.DescribeBulkCandidateComposition(candidates, members);

        StringAssert.Contains(text, "全部 2 条都属于当前池化成员");
        StringAssert.Contains(text, "成员本身仍留在池中");
        Assert.IsFalse(text.Contains("历史记录", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Composition_NoneOwned_SaysAllHistorical()
    {
        var candidates = new[] { Candidate("sub-x"), Candidate("sub-y") };
        var members = new[] { Member("scout", "sub-a") };

        var text = SubAgentTool.DescribeBulkCandidateComposition(candidates, members);

        StringAssert.Contains(text, "全部不属于现存池化成员");
        Assert.IsFalse(text.Contains("scout", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Composition_PoolUnavailable_SaysUndetermined_NotHistorical()
    {
        var candidates = new[] { Candidate("sub-a") };

        var text = SubAgentTool.DescribeBulkCandidateComposition(candidates, poolMembers: null);

        StringAssert.Contains(text, "无法判定");
        // 这一条是关键：把「不知道」渲染成「不是你的」，用户会据此放心删除。
        Assert.IsFalse(text.Contains("历史记录", StringComparison.Ordinal),
            $"池未就绪时不得声称是历史记录：{text}");
        Assert.IsFalse(text.Contains("全部不属于", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Composition_EmptyCandidates_RendersNothing()
    {
        var text = SubAgentTool.DescribeBulkCandidateComposition([], []);

        Assert.AreEqual(string.Empty, text,
            "候选为空时由调用方自己的文案说明「真正执行也只会清 0 条」，这里不得再补一句");
    }

    [TestMethod]
    public void Composition_MoreThanFiveDistinctMembers_TruncatesNamesWithSuffix()
    {
        var names = new[] { "m1", "m2", "m3", "m4", "m5", "m6" };
        var candidates = names.Select((_, i) => Candidate("sub-" + i)).ToArray();
        var members = names.Select((n, i) => Member(n, "sub-" + i)).ToArray();

        var text = SubAgentTool.DescribeBulkCandidateComposition(candidates, members);

        StringAssert.Contains(text, "m5");
        Assert.IsFalse(text.Contains("m6", StringComparison.Ordinal),
            $"超过 5 个只列前 5 个并加「等」：{text}");
        StringAssert.Contains(text, "等");
    }

    // ===== 辅助 =====

    private static SubAgentStatus Candidate(string subSessionId) => new()
    {
        RunId = subSessionId,
        ParentSessionId = ParentSessionId,
        SubSessionId = ParentSessionId + "-" + subSessionId,
        Status = "completed",
        TaskSummary = "historical",
        SpawnedAt = new DateTimeOffset(2026, 11, 20, 1, 2, 3, TimeSpan.FromHours(8)),
    };

    private static PooledSubAgent Member(string name, string subSessionId) => new()
    {
        PoolId = "poolid-" + name,
        Name = name,
        SubSessionId = ParentSessionId + "-" + subSessionId,
        TemplateId = "workspace-task-agent",
        CreatedAt = DateTimeOffset.UnixEpoch,
        LastUsedAt = new DateTimeOffset(2026, 11, 25, 9, 7, 5, TimeSpan.FromHours(8)),
        Status = PooledSubAgentStatus.Idle,
        TaskCount = 0,
        OwnerSessionId = ParentSessionId,
        OwnerWorkspaceId = "default",
    };
}
