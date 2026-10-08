using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 长循环上下文水位与 warm-prefix checkpoint 回归测试。
/// PrepareSoftCompaction 只负责选择候选区间；产品路径必须先用原请求 warm prefix
/// 生成摘要，再原子提交 checkpoint，禁止直接把选择结果写回历史。
/// </summary>
[TestClass]
public sealed class LlmRequestBudgetGuardTests
{
    [TestMethod]
    [DataRow(1_000L, 1_000)]
    [DataRow(0L, 27_976)]
    [DataRow(100_000L, 27_976)]
    [DataRow(long.MaxValue, 27_976)]
    public void EffectiveLimit_CombinesFrozenCapacityWithModelWindow(long capacity, int expected)
    {
        Assert.AreEqual(expected, LlmRequestBudgetGuard.ResolveEffectiveInputLimit(
            BuildConfig(), workUnitInputCapacity: capacity));
        Assert.AreEqual(123, LlmRequestBudgetGuard.ResolveEffectiveInputLimit(null, workUnitInputCapacity: 123));
    }

    [TestMethod]
    public void Prepare_WorkUnitCapacityTrimsHistoryWithoutChangingProtectedTail()
    {
        var history = BuildHistory(30);
        var expectedTail = history.TakeLast(8).Select(m => m.Content).ToArray();
        var result = LlmRequestBudgetGuard.Prepare(new ContextUsageSnapshotStore(), "capacity-session",
            history, null, BuildConfig(), workUnitInputCapacity: 15_000);
        Assert.AreEqual(15_000, result.EffectiveInputLimit);
        Assert.IsGreaterThan(0, result.RemovedMessageCount);
        CollectionAssert.AreEqual(expectedTail, result.Messages.TakeLast(8).Select(m => m.Content).ToArray());
        Assert.IsLessThanOrEqualTo(15_000, result.Snapshot.UsedTokens);
    }

    [TestMethod]
    public void Prepare_ProtectedContentOverCapacityFailsBeforeProviderCall()
    {
        var history = BuildHistory(2);
        Assert.ThrowsExactly<LlmInputBudgetExceededException>(() => LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(), "protected-session", history, null, BuildConfig(),
            workUnitInputCapacity: 100));
    }

    [TestMethod]
    public void SoftCompaction_NeverDeletesCurrentUserEvenWithManyToolMessages()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "system"), new(ChatRole.User, Big) };
        for (var i = 0; i < 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, Big));
        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(new ContextUsageSnapshotStore(),
            "protected-current-turn", messages, null, BuildConfig(), triggerRatio: 0.01, targetRatio: 0.005);
        Assert.IsFalse(result.Compacted);
        Assert.AreEqual(messages.Count, result.Messages.Count);
        Assert.AreEqual(ChatRole.User, result.Messages[1].Role);
    }

    private static readonly string Big = new('a', 4_000);

    private static LlmConfig BuildConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 30_000,
        MaxOutputTokens = 1_000,
    };

    private static List<ChatMessage> BuildHistory(int pairs)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt") };
        for (var i = 0; i < pairs; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"{Big} user-{i}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"{Big} assistant-{i}"));
        }
        return messages;
    }

    [TestMethod]
    public void PrepareSoftCompaction_NoOp_BelowTriggerRatio()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 2);

        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", history, tools: null, BuildConfig());

        Assert.IsFalse(result.Compacted);
        Assert.AreEqual(0, result.RemovedMessageCount);
        Assert.AreEqual(history.Count, result.Messages.Count);
        Assert.AreEqual(result.InitialUsedTokens, result.Snapshot.UsedTokens);
    }

    [TestMethod]
    public void PrepareSoftCompaction_EvictsUntilTarget_AndKeepsSystemAndTail()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var expectedTail = history.TakeLast(8).Select(static m => m.Content).ToList();

        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", history, tools: null, BuildConfig());

        Assert.IsTrue(result.Compacted, $"initial={result.InitialUsedTokens} limit={result.EffectiveInputLimit}");
        Assert.IsTrue(result.RemovedMessageCount > 0);
        Assert.IsTrue(result.Snapshot.UsedTokens < result.InitialUsedTokens);
        Assert.AreEqual(history.Count, result.InitialMessageCount);
        Assert.IsTrue(result.Messages.Count < history.Count);

        // System 消息永远保留；保留部分足以覆盖受保护尾部 8 条。
        Assert.AreEqual(ChatRole.System, result.Messages[0].Role);
        Assert.IsTrue(result.Messages.Count >= 9, $"kept={result.Messages.Count}");
        var actualTail = result.Messages.TakeLast(8).Select(static m => m.Content).ToList();
        CollectionAssert.AreEqual(expectedTail, actualTail);

        // 压缩后处于目标水位之下，下一轮软压缩应为 no-op（锯齿下沿）。
        var second = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", result.Messages, tools: null, BuildConfig());
        Assert.IsFalse(second.Compacted);
    }

    [TestMethod]
    public void PrepareSoftCompaction_NeverThrows_WhenNothingRemovable()
    {
        var store = new ContextUsageSnapshotStore();
        var hugeSystem = new List<ChatMessage> { new(ChatRole.System, new string('s', 40_000)) };

        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", hugeSystem, tools: null, BuildConfig());

        Assert.IsFalse(result.Compacted);
        Assert.AreEqual(0, result.RemovedMessageCount);
        Assert.AreEqual(1, result.Messages.Count);
    }

    [TestMethod]
    public void WarmPrefixPlan_ReplaysExactOutbound_AndAppendsOnlyInstruction()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);

        var created = WarmPrefixCompaction.TryCreatePlan(
            store,
            "session-1",
            history,
            history,
            tools: null,
            BuildConfig(),
            triggerRatio: 0.65,
            targetRatio: 0.5,
            out var plan);

        Assert.IsTrue(created);
        Assert.IsNotNull(plan);
        Assert.AreEqual(history.Count + 1, plan.SummaryRequestMessages.Count);
        for (var index = 0; index < history.Count; index++)
        {
            Assert.AreEqual(history[index], plan.SummaryRequestMessages[index],
                $"message {index} must be replayed byte-for-byte");
        }
        Assert.AreEqual(ChatRole.User, plan.SummaryRequestMessages[^1].Role);
        Assert.AreEqual(WarmPrefixCompaction.SummaryInstruction, plan.SummaryRequestMessages[^1].Content);
    }

    [TestMethod]
    public void WarmPrefixCheckpoint_ReplacesOldHeadOnce_AndPreservesSystemAndTail()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var expectedTail = history.TakeLast(8).ToArray();
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            store,
            "session-1",
            history,
            history,
            tools: null,
            BuildConfig(),
            triggerRatio: 0.65,
            targetRatio: 0.5,
            out var plan));

        var checkpoint = WarmPrefixCompaction.TryCreateCheckpoint(
            plan!,
            "## Primary Request and Intent\n- continue implementation\n\n## Next Step\n- run tests");

        Assert.IsNotNull(checkpoint);
        Assert.AreEqual(history[0], checkpoint[0], "system prompt bytes must remain unchanged");
        Assert.AreEqual(ChatRole.User, checkpoint[1].Role);
        StringAssert.Contains(checkpoint[1].Content, "<compacted-summary>");
        CollectionAssert.AreEqual(
            expectedTail,
            checkpoint.TakeLast(8).ToArray(),
            "protected recent messages must remain byte-for-byte after the checkpoint");
        Assert.IsTrue(checkpoint.Count < history.Count);
    }

    [TestMethod]
    public void WarmPrefixCheckpoint_RejectsEmptyOrNonShrinkingSummary()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            store,
            "session-1",
            history,
            history,
            tools: null,
            BuildConfig(),
            triggerRatio: 0.65,
            targetRatio: 0.5,
            out var plan));

        Assert.IsNull(WarmPrefixCompaction.TryCreateCheckpoint(plan!, ""));
        var strictPlan = plan! with { RemovedTokenEstimate = 1 };
        Assert.IsNull(WarmPrefixCompaction.TryCreateCheckpoint(
            strictPlan,
            "two tokens"));
    }

    /// <summary>
    /// ADR-095 D2 准入判据：候选只来自软阈值时不得进入发送关键路径。
    /// 关键不变量——判为「软」意味着出站硬门禁不会裁剪请求副本；判为「硬」时不同步压缩
    /// 就只能靠请求副本硬裁剪（丢会话级记忆连续性）。
    /// </summary>
    [TestMethod]
    public void WarmPrefixPlan_ClassifiesSoftVersusHardByRealHeadroom()
    {
        var history = BuildHistory(pairs: 40);
        var used = new ContextUsageSnapshotStore()
            .CaptureLlmRequest("probe", history, tools: null, "test-model").UsedTokens;
        Assert.IsGreaterThan(0, used);

        // ── 软：估算输入 ≈ 80% 有效上限（已达 0.65 触发比，但仍安全）──
        var softLimit = (int)(used / 0.8);
        var softConfig = ConfigForEffectiveLimit(softLimit);
        Assert.AreEqual(softLimit, LlmRequestBudgetGuard.ResolveEffectiveInputLimit(softConfig));

        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            new ContextUsageSnapshotStore(), "session-soft", history, history, tools: null, softConfig,
            triggerRatio: 0.65, targetRatio: 0.5, out var softPlan));
        Assert.IsNotNull(softPlan);
        Assert.IsFalse(
            softPlan!.RequiresSynchronousProtection,
            $"used={softPlan.InitialUsedTokens} limit={softPlan.EffectiveInputLimit}");
        Assert.IsGreaterThan(0, softPlan.HardHeadroomTokens);

        var softOutbound = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(), "session-soft", history, tools: null, softConfig);
        Assert.AreEqual(
            0,
            softOutbound.RemovedMessageCount,
            "判定为软维护时出站请求必须仍在硬预算内，否则同步保护不可省");

        // ── 硬：估算输入 ≈ 2× 有效上限 ──
        var hardLimit = used / 2;
        var hardConfig = ConfigForEffectiveLimit(hardLimit);
        Assert.AreEqual(hardLimit, LlmRequestBudgetGuard.ResolveEffectiveInputLimit(hardConfig));

        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            new ContextUsageSnapshotStore(), "session-hard", history, history, tools: null, hardConfig,
            triggerRatio: 0.65, targetRatio: 0.5, out var hardPlan));
        Assert.IsNotNull(hardPlan);
        Assert.IsTrue(hardPlan!.RequiresSynchronousProtection);
        Assert.IsLessThan(0, hardPlan.HardHeadroomTokens);

        var hardOutbound = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(), "session-hard", history, tools: null, hardConfig);
        Assert.IsGreaterThan(
            0,
            hardOutbound.RemovedMessageCount,
            "越界时若不同步压缩，出站只能靠请求副本硬裁剪");
    }

    /// <summary>
    /// 软阈值分母必须与出站硬门禁同源：WorkUnit 输入容量参与后，触发判断不得继续用更宽的模型窗口。
    /// </summary>
    [TestMethod]
    public void PrepareSoftCompaction_AppliesWorkUnitCapacityToTriggerDenominator()
    {
        var history = BuildHistory(pairs: 40);
        // 无 MaxContextTokens：有效上限完全由 WorkUnit 容量决定，断言可直接对号。
        var config = new LlmConfig { ModelId = "test-model" };

        var constrained = LlmRequestBudgetGuard.PrepareSoftCompaction(
            new ContextUsageSnapshotStore(), "session-capacity", history, tools: null, config,
            workUnitInputCapacity: 5_000);
        Assert.AreEqual(5_000, constrained.EffectiveInputLimit);
        Assert.IsTrue(constrained.Compacted, "5,000 分母下估算输入已越过触发比");
        Assert.IsGreaterThan(0, constrained.RemovedMessageCount);

        // 对照：不给容量约束时同一历史不触发（分母退化为 int.MaxValue）。
        var unconstrained = LlmRequestBudgetGuard.PrepareSoftCompaction(
            new ContextUsageSnapshotStore(), "session-capacity", history, tools: null, config);
        Assert.IsFalse(unconstrained.Compacted);
    }

    /// <summary>
    /// checkpoint 正文必须携带共享合同登记的摘要标记：计量分层与 UI 摘要桶按该合同识别，
    /// 未登记的标记会被误计入「对话消息」（诊断 2026-10-07 §4.3）。
    /// </summary>
    [TestMethod]
    public void WarmPrefixCheckpoint_UsesTheSharedSummaryMarkerContract()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            store, "session-marker", history, history, tools: null, BuildConfig(),
            triggerRatio: 0.65, targetRatio: 0.5, out var plan));

        var checkpoint = WarmPrefixCompaction.TryCreateCheckpoint(plan!, "## Current Work\n- continue");

        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(ContextSummaryMarkers.ContainsMarker(checkpoint[1].Content));
        Assert.IsTrue(checkpoint[1].Content.Contains(
            ContextSummaryMarkers.WarmPrefixCheckpoint,
            StringComparison.Ordinal));
    }

    /// <summary>反推一个使有效输入上限恰好等于 <paramref name="limit"/> 的配置。</summary>
    private static LlmConfig ConfigForEffectiveLimit(int limit) => new()
    {
        ModelId = "test-model",
        MaxOutputTokens = 1_000,
        MaxContextTokens = limit + 1_000 + LlmRequestBudgetGuard.DefaultSafetyBufferTokens,
    };

    /// <summary>
    /// 分类回放：2026-10-07 事故两条样本（只保留整数，无会话内容）。
    /// <para>
    /// 事实来源：`D:\data\logs\system\pudding-20261007_023.log`
    /// 行 1547 / 2781 的 `[AgentExec:Compaction] … estimated=583691->326392 / …->430576`
    /// （即 `plan.InitialUsedTokens`），上限取同日志 21 处的 `inputLimit=605760`。
    /// </para>
    /// <para>
    /// 结论：**只有第一条**（583,691）判软可延期；第二条（625,824）判硬，仍须同步保护——
    /// 不得把两条都当成「省掉前置压缩」。更正记录见
    /// `Docs/00_changelog/2026Year/10/2026-10-08-软压缩移出主请求关键路径与压缩生命周期归因.md`「更正（2026-10-08）」。
    /// </para>
    /// </summary>
    [TestMethod]
    [DataRow(583_691, false, DisplayName = "事故第一条 583,691 < 605,760 ⇒ 软（延期）")]
    [DataRow(625_824, true, DisplayName = "事故第二条 625,824 > 605,760 ⇒ 硬（同步保护）")]
    public void WarmPrefixPlan_ReplaysRecordedIncidentSamples(int initialUsedTokens, bool expectedHard)
    {
        const int recordedEffectiveInputLimit = 605_760;
        const int recordedSoftTrigger = 484_608; // 0.80 × 605,760：旧软触发阈值

        var replayed = BuildRecordedSamplePlan() with
        {
            InitialUsedTokens = initialUsedTokens,
            EffectiveInputLimit = recordedEffectiveInputLimit,
        };

        Assert.AreEqual(expectedHard, replayed.RequiresSynchronousProtection);
        Assert.AreEqual(
            recordedEffectiveInputLimit - initialUsedTokens,
            replayed.HardHeadroomTokens);
        // 两条样本都越过了旧软触发阈值 ⇒ 旧路径下都会同步压缩（这正是被修复的等待来源）。
        Assert.IsGreaterThanOrEqualTo(recordedSoftTrigger, initialUsedTokens);
    }

    /// <summary>回放用的判据载体：字段会被 <c>with</c> 覆盖，只需是一个真实构造出来的 plan。</summary>
    private static WarmPrefixCompactionPlan BuildRecordedSamplePlan()
    {
        var history = BuildHistory(pairs: 40);
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            new ContextUsageSnapshotStore(), "session-replay", history, history, tools: null, BuildConfig(),
            triggerRatio: 0.65, targetRatio: 0.5, out var plan));
        Assert.IsNotNull(plan);
        return plan!;
    }

    [TestMethod]
    public void FrozenSystemPrompt_MaintainsSameEpochBytes_AndPreservesHydratedCheckpoint()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system-v1"),
            new(ChatRole.User, "hello"),
        };

        var update = AgentExecutionService.EnsureFrozenSystemPrompt(history, "system-v2");
        Assert.AreEqual(SystemPromptUpdateKind.Replaced, update);
        Assert.AreEqual("system-v2", history[0].Content);

        var frozenMessage = history[0];
        update = AgentExecutionService.EnsureFrozenSystemPrompt(history, "system-v2");
        Assert.AreEqual(SystemPromptUpdateKind.Unchanged, update);
        Assert.AreSame(frozenMessage, history[0], "same epoch must retain exact message bytes/object");

        var hydrated = new List<ChatMessage>
        {
            new(ChatRole.System, "<compact_summary>prior facts</compact_summary>"),
            new(ChatRole.User, "continue"),
        };
        update = AgentExecutionService.EnsureFrozenSystemPrompt(hydrated, "system-v2");
        Assert.AreEqual(SystemPromptUpdateKind.Inserted, update);
        Assert.AreEqual("system-v2", hydrated[0].Content);
        Assert.AreEqual("<compact_summary>prior facts</compact_summary>", hydrated[1].Content);
    }

    [TestMethod]
    public void RuntimeExecutionConfig_Seeds_And_Clamps_SoftCompactionRatios()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-exec-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            // 全新根：自动补齐默认值。
            var service = new RuntimeExecutionConfigService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<RuntimeExecutionConfigService>.Instance);
            var seeded = service.GetOptions().SubAgents;
            Assert.AreEqual(0.80, seeded.ContextSoftCompactionTriggerRatio, 0.0001);
            Assert.AreEqual(0.5, seeded.ContextSoftCompactionTargetRatio, 0.0001);

            // 非法配置：trigger > 1、target > trigger —— 必须被夹取回有效区间。
            var configPath = Path.Combine(root, "config", "runtime.execution.json");
            File.WriteAllText(configPath, """
            {
              "subAgents": {
                "contextSoftCompactionTriggerRatio": 5.0,
                "contextSoftCompactionTargetRatio": 1.5
              }
            }
            """);
            var repaired = new RuntimeExecutionConfigService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<RuntimeExecutionConfigService>.Instance).GetOptions().SubAgents;
            Assert.AreEqual(1.0, repaired.ContextSoftCompactionTriggerRatio, 0.0001);
            Assert.AreEqual(1.0, repaired.ContextSoftCompactionTargetRatio, 0.0001);
            Assert.IsTrue(repaired.ContextSoftCompactionTargetRatio <= repaired.ContextSoftCompactionTriggerRatio);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
