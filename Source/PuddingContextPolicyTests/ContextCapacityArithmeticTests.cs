using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

/// <summary>
/// 容量算术与压力分类。含 2026-10-07 事故两条样本的**记录值**回放：判据输入取日志事实
/// （`[AgentExec:Compaction] … estimated=583691 / 625824`），上限取同日志 `inputLimit=605760`。
/// </summary>
[TestClass]
public sealed class ContextCapacityArithmeticTests
{
    // 事故运行时冻结的容量参数：模型窗口 1,000,000、资源池 deepseek-flash maxOutputTokens=393,216、
    // 安全余量 1,024（与出站硬门禁同源）。
    private static readonly ContextCapacityInputs IncidentInputs =
        new(ModelWindowTokens: 1_000_000, RequestedOutputBudgetTokens: 393_216, SafetyBufferTokens: 1_024);

    [TestMethod]
    public void Resolve_MatchesRecordedIncidentEffectiveLimit()
    {
        var capacity = ContextCapacityArithmetic.Resolve(IncidentInputs);

        // 与日志 21 处 inputLimit=605760 完全一致。
        Assert.AreEqual(605_760, capacity.EffectiveInputTokens);
        Assert.AreEqual(393_216, capacity.RequestedOutputBudgetTokens);
        Assert.AreEqual(393_216, capacity.ReservedOutputTokens);
        Assert.AreEqual(1_024, capacity.SafetyBufferTokens);
        Assert.IsNull(capacity.ProviderInputLimitTokens);
        Assert.AreEqual(
            ContextEffectiveWindowSources.OutputReserveAndSafetyMargin,
            capacity.EffectiveWindowSource);
    }

    /// <summary>
    /// 事故样本分类回放：**只有第一条**达软阈值且仍安全（可延期），第二条已越过硬边界。
    /// </summary>
    [TestMethod]
    [DataRow(583_691, ContextPressureState.SoftEligible, 22_069, DisplayName = "第一条 583,691 ⇒ 软（延期）")]
    [DataRow(625_824, ContextPressureState.HardProtectionExceeded, -20_064, DisplayName = "第二条 625,824 ⇒ 硬（同步保护）")]
    public void Classify_ReplaysRecordedIncidentSamples(
        int usedTokens,
        ContextPressureState expectedState,
        int expectedHeadroom)
    {
        var capacity = ContextCapacityArithmetic.Resolve(IncidentInputs);

        var decision = ContextCapacityArithmetic.Classify(usedTokens, capacity, softTriggerRatio: 0.80);

        Assert.AreEqual(expectedState, decision.State);
        Assert.AreEqual(expectedHeadroom, decision.HeadroomTokens);
        // 旧软触发阈值 0.80 × 605,760 = 484,608：两条样本都曾越过它（旧路径下都会同步压缩）。
        Assert.AreEqual(484_608, decision.SoftTriggerTokens);
        Assert.IsGreaterThanOrEqualTo(484_608, usedTokens);
        Assert.AreEqual(expectedState == ContextPressureState.SoftEligible, decision.CanDeferSoftMaintenance);
        Assert.AreEqual(expectedState == ContextPressureState.HardProtectionExceeded, decision.RequiresSynchronousProtection);
    }

    /// <summary>软阈值已定时，硬边界判定不受软比例影响：越界永远是硬保护。</summary>
    [TestMethod]
    public void Classify_HardBoundaryIsIndependentOfSoftTriggerRatio()
    {
        var capacity = ContextCapacityArithmetic.Resolve(IncidentInputs);

        var decision = ContextCapacityArithmetic.Classify(605_761, capacity, softTriggerRatio: 1.0);

        Assert.IsTrue(decision.RequiresSynchronousProtection);
        Assert.AreEqual("hard_input_budget_exceeded", decision.Reason);
    }

    [TestMethod]
    public void Classify_BelowTriggerDoesNothing()
    {
        var capacity = ContextCapacityArithmetic.Resolve(IncidentInputs);

        var decision = ContextCapacityArithmetic.Classify(100_000, capacity, softTriggerRatio: 0.80);

        Assert.AreEqual(ContextPressureState.BelowSoftTrigger, decision.State);
        Assert.IsFalse(decision.CanDeferSoftMaintenance);
        Assert.IsFalse(decision.RequiresSynchronousProtection);
    }

    [TestMethod]
    public void Resolve_ProviderInputLimitWinsAndIsReported()
    {
        var capacity = ContextCapacityArithmetic.Resolve(
            IncidentInputs with { ProviderInputLimitTokens = 200_000 });

        Assert.AreEqual(200_000, capacity.EffectiveInputTokens);
        Assert.AreEqual(200_000, capacity.ProviderInputLimitTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.ProviderInputLimit, capacity.EffectiveWindowSource);
    }

    /// <summary>
    /// 回退输出预算不得冒充用户预算：预算字段为 null，来源记为 fallback_output_reserve。
    /// </summary>
    [TestMethod]
    public void Resolve_FallbackOutputBudgetIsNotReportedAsConfiguredBudget()
    {
        var capacity = ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(
            ModelWindowTokens: 200_000,
            RequestedOutputBudgetTokens: null,
            SafetyBufferTokens: 1_024,
            FallbackOutputBudgetTokens: 2_048));

        Assert.IsNull(capacity.RequestedOutputBudgetTokens);
        Assert.AreEqual(2_048, capacity.ReservedOutputTokens);
        Assert.AreEqual(200_000 - 2_048 - 1_024, capacity.EffectiveInputTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.FallbackOutputReserve, capacity.EffectiveWindowSource);
    }

    [TestMethod]
    public void Resolve_NoReservationMeansModelWindowIsTheOnlySource()
    {
        var capacity = ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(ModelWindowTokens: 200_000));

        Assert.AreEqual(200_000, capacity.EffectiveInputTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.ModelWindow, capacity.EffectiveWindowSource);
        Assert.IsNull(capacity.RequestedOutputBudgetTokens);
    }

    [TestMethod]
    public void Resolve_SafetyMarginOnlyIsNamedExplicitly()
    {
        var capacity = ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(
            ModelWindowTokens: 200_000, SafetyBufferTokens: 1_024));

        Assert.AreEqual(200_000 - 1_024, capacity.EffectiveInputTokens);
        Assert.AreEqual(ContextEffectiveWindowSources.SafetyMargin, capacity.EffectiveWindowSource);
    }
}
