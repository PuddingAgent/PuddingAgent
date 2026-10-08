namespace PuddingContextPolicy;

/// <summary>
/// 容量算术（纯函数）：把「模型窗口 / Provider 输入上限 / 实际输出预算 / 安全余量」折算成
/// **有效输入上限**，并给出受限来源。
/// <para>
/// 不变量（与出站硬门禁同源，UI 与门禁必须共用同一算式与冻结参数）：
/// <c>effectiveInput = min(providerInputLimit, modelWindow − reservedOutput − safetyBuffer)</c>，
/// 其中 <c>reservedOutput = requestedOutputBudget ?? fallbackOutputBudget</c>。
/// </para>
/// <para>
/// 为什么回退值要单独记：门禁在容量没给输出预算时会退回一个内置预留。该值**不是**用户预算，
/// 若不区分，UI 会把「内置回退预留」显示成「用户配置的预留输出」（诊断 2026-10-07 §4.4）。
/// </para>
/// </summary>
public static class ContextCapacityArithmetic
{
    /// <summary>折算有效输入上限与来源。</summary>
    public static ContextCapacityResult Resolve(ContextCapacityInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var modelWindow = Math.Max(1, inputs.ModelWindowTokens);
        var safetyBuffer = Math.Max(0, inputs.SafetyBufferTokens);
        var requestedOutput = inputs.RequestedOutputBudgetTokens is > 0
            ? inputs.RequestedOutputBudgetTokens.Value
            : 0;
        var fallbackOutput = Math.Max(0, inputs.FallbackOutputBudgetTokens);
        // 实际参与算术的预留：真实预算优先，其次回退预留（两者语义不同，来源要区分）。
        var reservedOutput = requestedOutput > 0 ? requestedOutput : fallbackOutput;

        var providerLimitKnown = inputs.ProviderInputLimitTokens is > 0;
        var providerLimit = providerLimitKnown ? inputs.ProviderInputLimitTokens!.Value : int.MaxValue;

        var modelDerivedLimit = Math.Max(1, modelWindow - reservedOutput - safetyBuffer);
        var providerConstrained = providerLimit < modelDerivedLimit;
        var effectiveInput = Math.Max(1, Math.Min(modelDerivedLimit, providerLimit));

        var source = ResolveSource(providerConstrained, requestedOutput, fallbackOutput, safetyBuffer);

        return new ContextCapacityResult(
            modelWindow,
            providerLimitKnown ? providerLimit : null,
            requestedOutput > 0 ? requestedOutput : null,
            reservedOutput,
            safetyBuffer,
            effectiveInput,
            source);
    }

    /// <summary>
    /// 压力分类：先判硬边界，再判软阈值。Provider 输入上限取小后的有效上限是唯一分母。
    /// </summary>
    /// <param name="usedTokens">本次请求的估算输入（**口径由调用方保证**：与 <see cref="ContextCapacityResult.EffectiveInputTokens"/> 同源同校准）。</param>
    /// <param name="capacity">容量结果。</param>
    /// <param name="softTriggerRatio">软触发比例（默认 0.80 来自既有配置，不在组件里另设常量）。</param>
    public static ContextPressureDecision Classify(
        int usedTokens,
        ContextCapacityResult capacity,
        double softTriggerRatio)
    {
        ArgumentNullException.ThrowIfNull(capacity);

        var used = Math.Max(0, usedTokens);
        var limit = capacity.EffectiveInputTokens;
        var ratio = softTriggerRatio is > 0 and <= 1 ? softTriggerRatio : 1.0;
        var softTrigger = (int)Math.Ceiling(limit * ratio);
        var headroom = limit - used;

        if (used > limit)
        {
            return new ContextPressureDecision(
                ContextPressureState.HardProtectionExceeded,
                used,
                limit,
                softTrigger,
                headroom,
                "hard_input_budget_exceeded");
        }

        if (used >= softTrigger)
        {
            return new ContextPressureDecision(
                ContextPressureState.SoftEligible,
                used,
                limit,
                softTrigger,
                headroom,
                "soft_threshold_request_still_safe");
        }

        return new ContextPressureDecision(
            ContextPressureState.BelowSoftTrigger,
            used,
            limit,
            softTrigger,
            headroom,
            "below_soft_threshold");
    }

    private static string ResolveSource(
        bool providerConstrained,
        int requestedOutput,
        int fallbackOutput,
        int safetyBuffer)
    {
        if (providerConstrained)
            return ContextEffectiveWindowSources.ProviderInputLimit;

        var realOutputBudget = requestedOutput > 0;
        var outputReserveApplied = realOutputBudget || fallbackOutput > 0;
        if (outputReserveApplied && !realOutputBudget)
        {
            // 回退预留（可能叠加安全余量）：不得声称是用户配置的「预留输出」。
            return ContextEffectiveWindowSources.FallbackOutputReserve;
        }

        var hasSafetyBuffer = safetyBuffer > 0;
        if (realOutputBudget && hasSafetyBuffer)
            return ContextEffectiveWindowSources.OutputReserveAndSafetyMargin;
        if (realOutputBudget)
            return ContextEffectiveWindowSources.OutputReserve;
        if (hasSafetyBuffer)
            return ContextEffectiveWindowSources.SafetyMargin;
        return ContextEffectiveWindowSources.ModelWindow;
    }
}
