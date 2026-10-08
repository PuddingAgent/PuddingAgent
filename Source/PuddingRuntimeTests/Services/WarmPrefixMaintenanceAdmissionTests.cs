using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// F3 维护准入：软维护每 dispatch 一次（候选级），**硬保护逐轮重新评估**。
/// 用户点名的场景就在这里：第一轮软维护被延期后，后续工具结果把请求推过硬阈值，
/// 硬保护必须仍然有效 —— 不得被 Turn 级的一次性封顶屏蔽。
/// </summary>
[TestClass]
public sealed class WarmPrefixMaintenanceAdmissionTests
{
    private const int HistoryWithMaintenance = 40;

    /// <summary>事故场景：第 0 轮软维护已评估（被延期）⇒ 后续轮次越界时仍必须允许硬保护。</summary>
    [TestMethod]
    public void SoftDeferred_DoesNotBlockHardProtectionInLaterRounds()
    {
        // 第 0 轮：软维护评估过、但只是延期（没有真正压缩），硬保护尚未尝试。
        const bool softAttempted = true;
        const bool hardProtectionAttempted = false;

        Assert.IsFalse(
            WarmPrefixMaintenanceAdmission.ShouldAttemptSoft(HistoryWithMaintenance, softAttempted),
            "同一 dispatch 内的软维护已经评估过，不重复评估");

        Assert.IsTrue(
            WarmPrefixMaintenanceAdmission.ShouldAttemptHardProtection(
                HistoryWithMaintenance, hardProtectionAttempted),
            "软维护延期之后，后续轮次必须仍能触发硬保护（diagnosed 2026-10-07 §5.2 的退化路径）");
    }

    /// <summary>硬保护已尝试过 ⇒ 同一 dispatch 内不再重复（避免每轮都做昂贵测量）。</summary>
    [TestMethod]
    public void HardProtection_IsAttemptedAtMostOncePerDispatch()
    {
        Assert.IsTrue(
            WarmPrefixMaintenanceAdmission.ShouldAttemptHardProtection(HistoryWithMaintenance, hardProtectionAttemptedThisDispatch: false));
        Assert.IsFalse(
            WarmPrefixMaintenanceAdmission.ShouldAttemptHardProtection(HistoryWithMaintenance, hardProtectionAttemptedThisDispatch: true));
    }

    /// <summary>历史太短不值得压缩（与既有 history.Count &gt; 12 口径一致）。</summary>
    [TestMethod]
    [DataRow(WarmPrefixMaintenanceAdmission.MinHistoryMessages)]
    [DataRow(1)]
    [DataRow(0)]
    public void ShortHistory_NeverAttemptsMaintenance(int historyCount)
    {
        Assert.IsFalse(WarmPrefixMaintenanceAdmission.ShouldAttemptSoft(historyCount, softAttemptedThisDispatch: false));
        Assert.IsFalse(WarmPrefixMaintenanceAdmission.ShouldAttemptHardProtection(historyCount, hardProtectionAttemptedThisDispatch: false));
    }

    /// <summary>刚好超过阈值即可评估；边界值必须与既有口径一致。</summary>
    [TestMethod]
    public void JustAboveThreshold_AllowsBothEvaluations()
    {
        var count = WarmPrefixMaintenanceAdmission.MinHistoryMessages + 1;

        Assert.IsTrue(WarmPrefixMaintenanceAdmission.ShouldAttemptSoft(count, softAttemptedThisDispatch: false));
        Assert.IsTrue(WarmPrefixMaintenanceAdmission.ShouldAttemptHardProtection(count, hardProtectionAttemptedThisDispatch: false));
    }

    /// <summary>第 0 轮（尚无任何尝试）两条路径都可评估。</summary>
    [TestMethod]
    public void FirstRound_AllowsSoftAndHardEvaluation()
    {
        Assert.IsTrue(WarmPrefixMaintenanceAdmission.ShouldAttemptSoft(HistoryWithMaintenance, softAttemptedThisDispatch: false));
        Assert.IsTrue(WarmPrefixMaintenanceAdmission.ShouldAttemptHardProtection(HistoryWithMaintenance, hardProtectionAttemptedThisDispatch: false));
    }

    /// <summary>
    /// 不变量（本轮更正）：**Prepare 抛超限时，warm-prefix 压缩不可能救**。
    /// 两者共用同一个受保护尾部地板（同一个 ComputeProtectedTailStart），而 Prepare 还额外做
    /// 工具载荷驱逐（token 更少）⇒ 压缩后（地板 + 摘要）只会比裁剪后更大。
    /// 因此执行路径上的「异常时先压缩再重试」分支是死代码，已在 Streaming/Buffered 移除，
    /// 由本用例把这条不变量固定下来（防止有人再把那个分支加回来）。
    /// </summary>
    [TestMethod]
    public void ExceptionPath_WarmPrefixCannotRescue_WhenUntrimmableFloorExceedsLimit()
    {
        var store = new ContextUsageSnapshotStore();
        // 巨大 system（不可裁剪）+ 可裁剪中间段 + 受保护尾部；上限极小 ⇒ 裁到地板仍超限。
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, new string('s', 60_000)),
        };
        for (var i = 0; i < 20; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, new string('a', 4_000) + i));
            history.Add(new ChatMessage(ChatRole.Assistant, new string('a', 4_000) + i));
        }

        var config = new LlmConfig
        {
            ModelId = "test-model",
            MaxContextTokens = 12_000,
            MaxOutputTokens = 1_000,
        };

        // ① 硬预算裁剪到地板仍不够 ⇒ fail-closed。
        Assert.ThrowsExactly<LlmInputBudgetExceededException>(() =>
            LlmRequestBudgetGuard.Prepare(store, "session-floor", history, tools: null, config));

        // ② 候选仍能形成（中间段可裁剪），但加入摘要后整请求依然越界 ⇒ 应用边界必须拒绝。
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            store, "session-floor", history, history, tools: null, config,
            triggerRatio: 0.65, targetRatio: 0.5, out var plan));
        Assert.IsNotNull(plan);
        Assert.IsTrue(plan!.RequiresSynchronousProtection);

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, "session-floor", history, tools: null, config, plan, new string('x', 120));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("after_summary_exceeds_hard_limit", verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
    }
}
