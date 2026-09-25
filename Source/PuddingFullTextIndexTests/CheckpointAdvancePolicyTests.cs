using System.Reflection;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S2a：<see cref="CheckpointAdvancePolicy"/>（「本轮是否允许推进 checkpoint」的**纯策略接缝**）组件侧断言。
/// <para>
/// 依据（唯一权威）：方案 §2.4「某个文件失败、其他文件成功 ⇒ 成功文件可提交，但全局 checkpoint 不推进；
/// 失败文件和成功文件下次都会重放」+「成功文件被重复处理是允许的；漏掉文件不允许」，
/// 以及 §3.6 第 12 步与末句（取消 ⇒ 不写 checkpoint）。
/// </para>
/// <para>
/// 本文件**零 IO**：全部用例只构造 <see cref="FullTextMutationResult"/> 与纯类型，不读写任何文件。
/// </para>
/// </summary>
[TestClass]
public sealed class CheckpointAdvancePolicyTests
{
    private static readonly string ScopeRoot = Path.Combine(Path.GetTempPath(), "pudding-fts-s2a-scope");

    private static readonly DateTimeOffset ScanStart = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    /// <summary>上一轮已提交的 watermark（本轮扫描开始时刻晚于它）。</summary>
    private static readonly DateTimeOffset PreviousWatermark = ScanStart - TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Overlap = MTimeComparison.DefaultMTimeOverlap;

    // ── 判定核心：6 状态 × FailedCount{0,&gt;0} × RetainedOldCount{0,&gt;0} = 24 格 ──────

    /// <summary>
    /// ★ 全状态矩阵：<see cref="FullTextMutationState"/> 的 6 个取值 × <c>FailedCount ∈ {0, &gt;0}</c>
    /// × <c>RetainedOldCount ∈ {0, &gt;0}</c>，**逐格**断言「允许 / 不允许」（共 24 格，每格只判一次，
    /// 循环末尾用「格子数 == 24」自证全部被执行到）。
    /// <para>
    /// 期望值在此**独立重述**冻结规则（不调用被测策略推导），因此本断言不是同义反复：
    /// 允许推进 ⟺ <c>State == Applied</c> 且 <c>FailedCount == 0</c> 且 <c>RetainedOldCount == 0</c>。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Decide_StateMatrix_AllTwentyFourCells_AreEvaluatedAndFailClosed()
    {
        // 冻结晶态清单：新增终态时必须显式更新本片判定（不得静默多出一格）
        CollectionAssert.AreEqual(
            new[] { "Applied", "PartiallyApplied", "Rejected", "Busy", "Cancelled", "Failed" },
            Enum.GetNames<FullTextMutationState>(),
            "FullTextMutationState 的 6 个取值（按值排序）是本片矩阵的定义域");

        var cells = new Dictionary<(FullTextMutationState State, bool Failed, bool Retained), int>();
        var allowedCells = 0;
        var blockedCells = 0;

        foreach (var state in Enum.GetValues<FullTextMutationState>())
        {
            foreach (var failedCount in new[] { 0, 1 })
            {
                foreach (var retainedOldCount in new[] { 0, 1 })
                {
                    var result = Build(state, failedCount, retainedOldCount);
                    var decision = CheckpointAdvancePolicy.Decide(result);

                    var cell = (state, Failed: failedCount > 0, Retained: retainedOldCount > 0);
                    cells[cell] = cells.TryGetValue(cell, out var seen) ? seen + 1 : 1;

                    // 冻结规则（独立重述，不调用被测实现）
                    var expectedAllowed =
                        state == FullTextMutationState.Applied && failedCount == 0 && retainedOldCount == 0;

                    Assert.AreEqual(
                        expectedAllowed,
                        decision.Allowed,
                        $"格子 {cell} 的推进判定不符冻结规则（State={state}, FailedCount={failedCount}, RetainedOldCount={retainedOldCount}）");

                    Assert.AreEqual(
                        expectedAllowed,
                        CheckpointAdvancePolicy.AllowsAdvance(result),
                        $"格子 {cell} 的 AllowsAdvance 必须与 Decide 一致");

                    if (expectedAllowed)
                    {
                        allowedCells++;
                        Assert.AreEqual(
                            CheckpointAdvanceBlockReason.None,
                            decision.Reason,
                            $"允许推进的格子 {cell} 不得带阻止原因");
                    }
                    else
                    {
                        blockedCells++;
                        Assert.AreNotEqual(
                            CheckpointAdvanceBlockReason.None,
                            decision.Reason,
                            $"被阻止的格子 {cell} 必须给出原因");

                        if (state == FullTextMutationState.Applied)
                        {
                            var expectedReason = failedCount > 0
                                ? CheckpointAdvanceBlockReason.FailedItemsPresent
                                : CheckpointAdvanceBlockReason.RetainedOldItemsPresent;

                            Assert.AreEqual(expectedReason, decision.Reason, $"格子 {cell} 的原因必须精确");
                        }
                    }
                }
            }
        }

        Assert.AreEqual(24, cells.Count, "6 × 2 × 2 的每一格都必须被实际执行到（不得只测代表值）");
        Assert.AreEqual(24, cells.Values.Sum(), "每格只能被判定一次");
        Assert.AreEqual(1, allowedCells, "唯一允许推进的格子必须是 (Applied, FailedCount=0, RetainedOldCount=0)");
        Assert.AreEqual(23, blockedCells, "其余 23 格一律不允许推进（fail-closed）");
    }

    /// <summary>
    /// ★ 故障重放场景（核心不变量）：3 个文件成功 + 1 个文件失败 ⇒ **不允许推进**；
    /// 且因为 watermark 不变，下一轮 **4 个文件全部重放**（含已成功的 3 个 —— 重复处理允许，漏文件不允许）。
    /// <para>
    /// 同时给出**反向对照**：若错误地推进到本轮扫描开始时刻，失败文件的 mtime 早于新有效水位线 ⇒
    /// 会被**永久跳过**（这正是禁止推进的理由，也证明上面的「全部重放」不是恒真）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ThreeSucceededOneFailed_BlocksAdvance_AndAllFourFilesReplayNextRound()
    {
        var succeeded = new[]
        {
            Path.Combine(ScopeRoot, "a.cs"),
            Path.Combine(ScopeRoot, "b.cs"),
            Path.Combine(ScopeRoot, "c.cs"),
        };
        var failed = Path.Combine(ScopeRoot, "d.cs");

        // 4 个文件本轮都是候选：mtime >= 上一轮 watermark - overlap
        var observedMTime = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            [succeeded[0]] = PreviousWatermark,
            [succeeded[1]] = PreviousWatermark,
            [succeeded[2]] = PreviousWatermark,
            [failed] = PreviousWatermark + TimeSpan.FromSeconds(1),
        };

        foreach (var (path, mtime) in observedMTime)
        {
            Assert.IsTrue(
                MTimeComparison.RequiresProcessing(mtime, PreviousWatermark, Overlap),
                $"{path} 必须在本轮成为候选（前置条件）");
        }

        // 本轮：3 个成功（已提交）+ 1 个失败（保留旧文档，待重试）⇒ PartiallyApplied
        var result = Build(
            FullTextMutationState.PartiallyApplied,
            failedCount: 1,
            retainedOldCount: 1,
            upsertAppliedCount: 3,
            failedPaths: new[] { failed },
            retainedOldPaths: new[] { failed });

        var decision = CheckpointAdvancePolicy.Decide(result);

        Assert.IsFalse(
            decision.Allowed,
            "3 成功 + 1 失败 ⇒ 绝不允许推进 checkpoint（漏文件比重复处理严重得多）");
        Assert.AreEqual(CheckpointAdvanceBlockReason.PartiallyApplied, decision.Reason);

        // 后果断言：不允许推进 ⇒ watermark 保持上一轮的值 ⇒ 下一轮 4 个文件全部重放
        var nextRoundWatermark = PreviousWatermark;
        var replayed = new List<string>();
        foreach (var (path, mtime) in observedMTime)
        {
            if (MTimeComparison.RequiresProcessing(mtime, nextRoundWatermark, Overlap))
                replayed.Add(path);
        }

        CollectionAssert.AreEquivalent(
            observedMTime.Keys.ToArray(),
            replayed.ToArray(),
            "checkpoint 未推进 ⇒ 下一轮必须对全部 4 个文件（3 个成功 + 1 个失败）重放");
        Assert.AreEqual(4, replayed.Count);

        // 反向对照：错误推进会让失败文件被永久跳过
        var wronglyAdvancedWatermark = ScanStart;
        Assert.IsFalse(
            MTimeComparison.RequiresProcessing(observedMTime[failed], wronglyAdvancedWatermark, Overlap),
            "若推进 checkpoint，失败文件的 mtime 早于新有效水位线 ⇒ 永久漏文件（禁止推进的理由）");
        foreach (var path in succeeded)
        {
            Assert.IsFalse(
                MTimeComparison.RequiresProcessing(observedMTime[path], wronglyAdvancedWatermark, Overlap),
                "已成功文件在新水位线下本会被跳过 —— 允许（重复处理允许），但绝不能带着失败文件一起跳");
        }
    }

    /// <summary>
    /// <c>RetainedOldCount &gt; 0</c> **单独特测**：全成功但存在 1 个「保留旧索引 + 待重试」的路径 ⇒ 不允许推进。
    /// <para>理由与失败同构：推进会让该文件的 mtime 早于新 watermark ⇒ 待重试永远不发生（索引永久停留在旧内容）。</para>
    /// </summary>
    [TestMethod]
    public void RetainedOld_SinglePendingRetryPath_BlocksAdvance()
    {
        var retained = Path.Combine(ScopeRoot, "locked.cs");
        var result = Build(
            FullTextMutationState.Applied,
            failedCount: 0,
            retainedOldCount: 1,
            upsertAppliedCount: 3,
            retainedOldPaths: new[] { retained });

        var decision = CheckpointAdvancePolicy.Decide(result);

        Assert.IsFalse(
            decision.Allowed,
            "RetainedOldCount > 0 ⇒ 存在待重试路径 ⇒ 不允许推进（否则待重试永远不发生）");
        Assert.AreEqual(CheckpointAdvanceBlockReason.RetainedOldItemsPresent, decision.Reason);

        // 反向对照：同一个批次把 retained 清零后允许推进 ⇒ 证明阻止来自 retained 而不是别的条件
        var withoutRetained = Build(
            FullTextMutationState.Applied,
            failedCount: 0,
            retainedOldCount: 0,
            upsertAppliedCount: 4);
        Assert.IsTrue(CheckpointAdvancePolicy.Decide(withoutRetained).Allowed);
    }

    /// <summary>取消特测（方案 §3.6 末句：取消必须 rollback / 外抛，**不写 checkpoint**）。</summary>
    [TestMethod]
    public void CancelledBatch_BlocksAdvance()
    {
        var cancelled = Build(FullTextMutationState.Cancelled, failedCount: 0, retainedOldCount: 0);
        var decision = CheckpointAdvancePolicy.Decide(cancelled);

        Assert.IsFalse(decision.Allowed, "取消 ⇒ 不允许推进 checkpoint（§3.6 末句）");
        Assert.AreEqual(CheckpointAdvanceBlockReason.Cancelled, decision.Reason);

        // 取消导致的阻止与「失败导致的阻止」必须是不同原因（便于调用方分别处理重试与告警）
        Assert.AreNotEqual(decision.Reason, CheckpointAdvancePolicy.Decide(
            Build(FullTextMutationState.Applied, failedCount: 1, retainedOldCount: 0)).Reason);
    }

    /// <summary>
    /// **负向对照**：完全成功（<c>Applied</c> + 0 失败 + 0 保留）⇒ **允许**推进。
    /// <para>
    /// 没有这一条，把策略写成「永不推进」也能让上面所有「不允许」的断言全绿 = 假绿；
    /// 并且逐条翻转三个条件中的**任意一个**都必须变成「不允许」，证明规则是三条件合取而不是只看了状态。
    /// </para>
    /// </summary>
    [TestMethod]
    public void FullyAppliedBatch_IsTheOnlyAdvancingCase()
    {
        var fullyApplied = Build(FullTextMutationState.Applied, failedCount: 0, retainedOldCount: 0);
        var decision = CheckpointAdvancePolicy.Decide(fullyApplied);

        Assert.IsTrue(decision.Allowed, "完全成功必须允许推进（否则策略退化成「永不推进」）");
        Assert.AreEqual(CheckpointAdvanceBlockReason.None, decision.Reason);
        Assert.IsTrue(CheckpointAdvancePolicy.AllowsAdvance(fullyApplied));

        // 三个条件各自翻转 ⇒ 都必须变成「不允许」
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(Build(FullTextMutationState.PartiallyApplied, 0, 0)),
            "条件①（State == Applied）被破坏 ⇒ 不允许");
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(Build(FullTextMutationState.Applied, failedCount: 1, retainedOldCount: 0)),
            "条件②（FailedCount == 0）被破坏 ⇒ 不允许");
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(Build(FullTextMutationState.Applied, failedCount: 0, retainedOldCount: 1)),
            "条件③（RetainedOldCount == 0）被破坏 ⇒ 不允许");
    }

    /// <summary>
    /// 阻止原因必须**可区分**：失败 / 取消 / 预算拒绝 / 互斥 / 部分成功 / 状态异常给出的原因互不相同
    /// （把不同原因折叠成一个值会让诊断与修复失去依据）。
    /// </summary>
    [TestMethod]
    public void BlockReasons_AreDistinguishableAcrossFailureCancellationAndBudgetRejection()
    {
        var failing = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.Applied, failedCount: 1, retainedOldCount: 0));
        var cancelled = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.Cancelled, 0, 0));
        var budgetRejected = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.Rejected, 0, 0));
        var busy = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.Busy, 0, 0));
        var partial = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.PartiallyApplied, 0, 0));
        var fatal = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.Failed, 0, 0));
        var retained = CheckpointAdvancePolicy.Decide(Build(FullTextMutationState.Applied, failedCount: 0, retainedOldCount: 1));

        var observed = new[]
        {
            failing.Reason,
            cancelled.Reason,
            budgetRejected.Reason,
            busy.Reason,
            partial.Reason,
            fatal.Reason,
            retained.Reason,
        };

        Assert.AreEqual(7, observed.Distinct().Count(), "每个阻止原因都必须是独立取值（不得复用同一个值）");
        Assert.AreEqual(CheckpointAdvanceBlockReason.FailedItemsPresent, failing.Reason);
        Assert.AreEqual(CheckpointAdvanceBlockReason.Cancelled, cancelled.Reason);
        Assert.AreEqual(CheckpointAdvanceBlockReason.Rejected, budgetRejected.Reason);
        Assert.AreEqual(CheckpointAdvanceBlockReason.Busy, busy.Reason);
        Assert.AreEqual(CheckpointAdvanceBlockReason.PartiallyApplied, partial.Reason);
        Assert.AreEqual(CheckpointAdvanceBlockReason.Failed, fatal.Reason);
        Assert.AreEqual(CheckpointAdvanceBlockReason.RetainedOldItemsPresent, retained.Reason);
    }

    /// <summary>
    /// 契约自相矛盾时仍 **fail-closed**：<c>State == Applied</c> 但 <c>FailedCount &gt; 0</c> ⇒ 不允许推进；
    /// 且策略**不回读** <c>CheckpointAdvanced</c> 字段（它是本判定的产物，回读会造成循环依赖，
    /// 也会让一个陈旧标志覆盖真实计数）。
    /// </summary>
    [TestMethod]
    public void ContradictoryResult_IsFailClosed_AndTheLyingCheckpointAdvancedFlagIsIgnored()
    {
        var contradictory = Build(FullTextMutationState.Applied, failedCount: 1, retainedOldCount: 0);
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(contradictory),
            "State 说成功但 FailedCount > 0 ⇒ 必须按失败处理（fail-closed）");

        var lyingFlagTrue = WithCheckpointAdvanced(
            Build(FullTextMutationState.PartiallyApplied, failedCount: 1, retainedOldCount: 1),
            checkpointAdvanced: true);
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(lyingFlagTrue),
            "结果里的 CheckpointAdvanced=true 不得覆盖真实计数");

        var lyingFlagFalse = WithCheckpointAdvanced(
            Build(FullTextMutationState.Applied, failedCount: 0, retainedOldCount: 0),
            checkpointAdvanced: false);
        Assert.IsTrue(
            CheckpointAdvancePolicy.AllowsAdvance(lyingFlagFalse),
            "CheckpointAdvanced=false 也不得覆盖真实计数（该字段是本判定的产物，不是输入）");
    }

    /// <summary>
    /// 边界纪律：策略是**静态无状态**的纯函数层，公开入口恰好两个（<c>Decide</c> / <c>AllowsAdvance</c>），
    /// 不含任何静态字段（无缓存 / 无配置 / 无 IO 依赖），且阻止原因枚举名必须是 ASCII。
    /// </summary>
    [TestMethod]
    public void Policy_IsPureStaticWithFrozenSurface_AndAsciiReasonNames()
    {
        var type = typeof(CheckpointAdvancePolicy);

        Assert.IsTrue(type.IsAbstract && type.IsSealed, "策略必须是静态类（不允许实例状态）");

        var publicMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "AllowsAdvance", "Decide" },
            publicMethods,
            "公开接缝必须恰好是 Decide / AllowsAdvance 两个");

        Assert.AreEqual(
            0,
            type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly).Length,
            "策略不得持有任何静态字段（无缓存 / 无配置 / 无 IO 依赖）");

        var reasonNames = Enum.GetNames<CheckpointAdvanceBlockReason>();
        Assert.AreEqual(9, reasonNames.Length);
        foreach (var name in reasonNames)
        {
            Assert.IsTrue(
                name.All(char.IsAsciiLetter),
                $"原因枚举名必须是 ASCII 无歧义：{name}");
        }

        Assert.AreEqual(0, (int)CheckpointAdvanceBlockReason.None, "None 必须恒为 0（默认值即「无阻止原因」）");
        Assert.AreEqual(9, Enum.GetValues<CheckpointAdvanceBlockReason>().Distinct().Count());
    }

    /// <summary>两个公开入口都必须拒绝 null（fail-fast 的编程错误，而不是静默放行）。</summary>
    [TestMethod]
    public void Policy_RejectsNullResult()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointAdvancePolicy.Decide(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointAdvancePolicy.AllowsAdvance(null!));
    }

    // ── 构造帮助 ─────────────────────────────────────────────────────────

    private static FullTextMutationResult Build(
        FullTextMutationState state,
        int failedCount,
        int retainedOldCount,
        int upsertAppliedCount = 0,
        int deleteAppliedCount = 0,
        IReadOnlyList<string>? failedPaths = null,
        IReadOnlyList<string>? retainedOldPaths = null,
        bool checkpointAdvanced = false)
        => new(
            "01K-S2A-BATCH",
            ScopeRoot,
            state,
            upsertAppliedCount,
            deleteAppliedCount,
            retainedOldCount,
            failedCount,
            failedPaths ?? Array.Empty<string>(),
            retainedOldPaths ?? Array.Empty<string>(),
            null,
            null,
            null,
            checkpointAdvanced,
            null);

    private static FullTextMutationResult WithCheckpointAdvanced(FullTextMutationResult result, bool checkpointAdvanced)
        => result with { CheckpointAdvanced = checkpointAdvanced };
}
