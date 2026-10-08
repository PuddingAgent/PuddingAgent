using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextHealthEvaluatorTests
{
    [TestMethod]
    public void Evaluate_DefaultThreshold_Is0_80()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, which is < 0.80 → Warning
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(ContextHealthState.Warning, health.State);
        Assert.IsFalse(health.ShouldAutoCompact);
        Assert.IsFalse(health.ShouldBlockSend);
    }

    [TestMethod]
    public void Evaluate_BelowDefaultThreshold_IsWarning()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 115000 / 180000 = 0.639, which is < 0.80 but >= 0.60 → Warning
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 115_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(ContextHealthState.Warning, health.State);
        Assert.IsTrue(health.ShouldSuggestCompact);
        Assert.IsFalse(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void Evaluate_CustomThreshold_IsUsed()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 136800 / 180000 = 0.76
        // With threshold 0.80: 0.76 < 0.80, but >= 0.75 → Unhealthy
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 136_800,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0.80);

        Assert.AreEqual(ContextHealthState.Unhealthy, health.State);
        Assert.IsFalse(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void Evaluate_CustomThreshold_HigherTriggersAtHigherRatio()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 160000 / 180000 = 0.889
        // With threshold 0.85: 0.889 >= 0.85 → Critical
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 160_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0.85);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
        Assert.IsTrue(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void Evaluate_InvalidThreshold_Zero_FallsBackToDefault()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, threshold=0→fallback 0.80 → Warning
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0);

        Assert.AreEqual(ContextHealthState.Warning, health.State);
    }

    [TestMethod]
    public void Evaluate_InvalidThreshold_Negative_FallsBackToDefault()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, threshold=-0.5→fallback 0.80 → Warning
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: -0.5);

        Assert.AreEqual(ContextHealthState.Warning, health.State);
    }

    [TestMethod]
    public void Evaluate_InvalidThreshold_AboveOne_FallsBackToDefault()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, threshold=1.5→fallback 0.80 → Warning
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 1.5);

        Assert.AreEqual(ContextHealthState.Warning, health.State);
    }

    [TestMethod]
    public void Evaluate_ThresholdExactlyOne_Works()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 163800 / 180000 = 0.91, threshold=0.90 → Critical (0.91 >= 0.90, < 0.92 so not Blocking)
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 163_800,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0.90);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
    }

    /// <summary>
    /// 2026-09-22 超限事故的观测盲点回归：快照必须同时暴露门禁口径 GateRatio
    /// （分母 = 有效输入窗口），它在 effectiveWindow &lt; modelWindow 时与显示口径 UsageRatio 不相等。
    /// 事故实测：used=609305, modelWindow=1_000_000, effectiveWindow=606_784。
    /// </summary>
    [TestMethod]
    public void Evaluate_ExposesGateRatio_AgainstEffectiveWindow_NotModelWindow()
    {
        var evaluator = new ContextHealthEvaluator();

        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 609_305,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 384_000,
            safetyBufferTokens: 9_216,
            compactionThreshold: 0.80);

        Assert.AreEqual(606_784, health.EffectiveWindowTokens);
        Assert.AreEqual(609_305.0 / 606_784.0, health.GateRatio, 1e-9);
        Assert.AreEqual(609_305.0 / 1_000_000.0, health.UsageRatio, 1e-9);
        Assert.IsGreaterThan(1.0, health.GateRatio);
        Assert.AreNotEqual(health.UsageRatio, health.GateRatio);
        Assert.AreEqual(ContextHealthState.Blocking, health.State);
        Assert.IsTrue(health.ShouldBlockSend);

        // 门禁阈值随快照输出，且引用常量而非字面量。
        Assert.AreEqual(ContextHealthGateThresholds.WarningRatio, health.GateThresholds.Warning, 1e-9);
        Assert.AreEqual(ContextHealthGateThresholds.UnhealthyRatio, health.GateThresholds.Unhealthy, 1e-9);
        Assert.AreEqual(ContextHealthGateThresholds.TriggerRatio, health.GateThresholds.Trigger, 1e-9);
        Assert.AreEqual(ContextHealthGateThresholds.BlockingRatio, health.GateThresholds.Blocking, 1e-9);
        Assert.AreEqual(0.60, health.GateThresholds.Warning, 1e-9);
        Assert.AreEqual(0.75, health.GateThresholds.Unhealthy, 1e-9);
        Assert.AreEqual(0.80, health.GateThresholds.Trigger, 1e-9);
        Assert.AreEqual(0.92, health.GateThresholds.Blocking, 1e-9);
    }

    /// <summary>
    /// 无预留（输出 0、缓冲 0）时两个口径重合；有预留时 Trigger 阈值必须回写实际生效值。
    /// </summary>
    [TestMethod]
    public void Evaluate_GateRatioAndThresholds_FollowReservationAndConfiguredTrigger()
    {
        var evaluator = new ContextHealthEvaluator();

        var noReservation = evaluator.Evaluate("session-1", 50_000, 200_000, maxOutputTokens: 0);
        Assert.AreEqual(0.25, noReservation.GateRatio, 1e-12);
        Assert.AreEqual(noReservation.UsageRatio, noReservation.GateRatio, 1e-12);

        // 有效窗口变小 ⇒ 门禁比率高于显示比率（两者不再相等）。
        var reserved = evaluator.Evaluate("session-1", 50_000, 200_000, maxOutputTokens: 50_000);
        Assert.AreEqual(150_000, reserved.EffectiveWindowTokens);
        Assert.AreEqual(50_000.0 / 150_000.0, reserved.GateRatio, 1e-12);
        Assert.AreEqual(0.25, reserved.UsageRatio, 1e-12);
        Assert.AreNotEqual(reserved.UsageRatio, reserved.GateRatio);

        // 配置了非默认压缩阈值时，输出必须回写实际生效值（而不是常量默认值）。
        var custom = evaluator.Evaluate(
            "session-1", 50_000, 200_000, maxOutputTokens: 50_000, compactionThreshold: 0.85);
        Assert.AreEqual(0.85, custom.GateThresholds.Trigger, 1e-9);
        Assert.AreEqual(0.60, custom.GateThresholds.Warning, 1e-9);
    }

    /// <summary>
    /// 方案 §2.5 / 诊断 §4.4：容量 DTO 必须分别输出模型窗口、Provider 输入上限、实际请求输出预算、
    /// 安全余量与有效输入上限**及各自来源**。UI 不得再用 `windowLimit − effectiveLimit` 猜「预留输出」。
    /// </summary>
    [TestMethod]
    public void Evaluate_ReportsCapacityComponents_ForRealOutputBudgetPlusSafetyMargin()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            "session-1",
            usedTokens: 50_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 50_000,
            safetyBufferTokens: 1_024,
            requestedOutputBudgetTokens: 50_000);

        Assert.AreEqual(200_000, health.ContextWindowTokens);
        Assert.AreEqual(200_000 - 50_000 - 1_024, health.EffectiveWindowTokens);
        Assert.IsNull(health.ProviderInputLimitTokens, "未配置 Provider 输入上限时必须为 null，不伪造");
        Assert.AreEqual(50_000, health.RequestedOutputBudgetTokens);
        Assert.AreEqual(1_024, health.SafetyBufferTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.OutputReserveAndSafetyMargin, health.EffectiveWindowSource);
    }

    /// <summary>Provider 输入上限参与取小时，来源必须指向它，且上报该上限。</summary>
    [TestMethod]
    public void Evaluate_ProviderInputLimitWins_AsEffectiveWindowSource()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            "session-1",
            usedTokens: 10_000,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 384_000,
            safetyBufferTokens: 1_024,
            maxInputTokens: 200_000,
            requestedOutputBudgetTokens: 384_000);

        // 模型窗口推导值 = 1,000,000 − 384,000 − 1,024 = 614,976 > 200,000 ⇒ Provider 上限生效。
        Assert.AreEqual(200_000, health.EffectiveWindowTokens);
        Assert.AreEqual(200_000, health.ProviderInputLimitTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.ProviderInputLimit, health.EffectiveWindowSource);
        Assert.AreEqual(384_000, health.RequestedOutputBudgetTokens);
    }

    /// <summary>
    /// 容量没给出输出预算时，门禁用的回退预留**不得**被报告成用户预算：
    /// 预算字段为 null，来源明确记为 fallback（诊断 §4.4 的 393.2K 归属问题）。
    /// </summary>
    [TestMethod]
    public void Evaluate_FallbackOutputReserve_IsNotReportedAsConfiguredBudget()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            "session-1",
            usedTokens: 10_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 2_048,
            safetyBufferTokens: 1_024,
            requestedOutputBudgetTokens: null,
            outputBudgetIsFallback: true);

        Assert.IsNull(health.RequestedOutputBudgetTokens, "回退值不是容量给出的预算");
        Assert.AreEqual(1_024, health.SafetyBufferTokens);
        Assert.AreEqual(200_000 - 2_048 - 1_024, health.EffectiveWindowTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.FallbackOutputReserve, health.EffectiveWindowSource);
    }

    /// <summary>没有任何预留时来源是模型窗口本身；只有安全余量时来源是安全余量。</summary>
    [TestMethod]
    public void Evaluate_NoReservationAndSafetyMarginOnly_SourcesAreExplicit()
    {
        var evaluator = new ContextHealthEvaluator();

        var noReservation = evaluator.Evaluate("session-1", 10_000, 200_000, maxOutputTokens: 0);
        Assert.AreEqual(200_000, noReservation.EffectiveWindowTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.ModelWindow, noReservation.EffectiveWindowSource);
        Assert.IsNull(noReservation.RequestedOutputBudgetTokens);
        Assert.IsNull(noReservation.ProviderInputLimitTokens);
        Assert.AreEqual(0, noReservation.SafetyBufferTokens);

        var safetyOnly = evaluator.Evaluate(
            "session-1", 10_000, 200_000, maxOutputTokens: 0, safetyBufferTokens: 1_024);
        Assert.AreEqual(200_000 - 1_024, safetyOnly.EffectiveWindowTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.SafetyMargin, safetyOnly.EffectiveWindowSource);
    }
}
