using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

public sealed class ContextHealthEvaluator
{
    /// <param name="requestedOutputBudgetTokens">
    /// 容量侧给出的**真实**输出预算，仅用于如实报告「预留输出」的来源。
    /// 省略且 <paramref name="maxOutputTokens"/> &gt; 0 时按 maxOutputTokens 为真实预算处理
    /// （保持既有调用者语义）。
    /// </param>
    /// <param name="outputBudgetIsFallback">
    /// 调用方是否**用回退值**计算了输出预留（容量没有给出实际预算）。为 true 时
    /// <c>RequestedOutputBudgetTokens</c> 报告为 null、来源记为
    /// <see cref="ContextEffectiveWindowSources.FallbackOutputReserve"/>，不得把回退值冒充用户预算
    /// （诊断 2026-10-07 §4.4）。C# 无法区分「省略」与「显式传 null」，故用独立开关而非哨兵值。
    /// </param>
    public ContextHealthSnapshot Evaluate(
        string sessionId,
        int usedTokens,
        int contextWindowTokens,
        int maxOutputTokens,
        int safetyBufferTokens = 0,
        int? maxInputTokens = null,
        double compactionThreshold = ContextCompactionDefaults.TriggerRatio,
        int? requestedOutputBudgetTokens = null,
        bool outputBudgetIsFallback = false)
    {
        var effectiveThreshold = compactionThreshold is > 0 and <= 1 ? compactionThreshold : ContextCompactionDefaults.TriggerRatio;
        var modelWindow = Math.Max(1, contextWindowTokens);
        var reservedOutput = Math.Max(0, maxOutputTokens);
        var safetyBuffer = Math.Max(0, safetyBufferTokens);
        var contextDerivedInputLimit = Math.Max(1, modelWindow - reservedOutput - safetyBuffer);
        var providerInputLimitKnown = maxInputTokens is > 0;
        var providerInputLimit = providerInputLimitKnown
            ? maxInputTokens!.Value
            : int.MaxValue;
        var providerConstrained = providerInputLimit < contextDerivedInputLimit;
        var effectiveWindow = Math.Max(1, Math.Min(contextDerivedInputLimit, providerInputLimit));
        var normalizedUsedTokens = Math.Max(0, usedTokens);
        var remaining = Math.Max(0, effectiveWindow - normalizedUsedTokens);
        // 是否把「预留」归因到真实预算：回退值不算用户预算；否则以显式传入值为准，
        // 未传时回落到 maxOutputTokens（既有调用者语义）。
        var reportedOutputBudget = outputBudgetIsFallback
            ? null
            : (requestedOutputBudgetTokens ?? (maxOutputTokens > 0 ? maxOutputTokens : null));
        var effectiveWindowSource = ResolveEffectiveWindowSource(
            providerConstrained,
            reportedOutputBudget,
            safetyBuffer,
            reservedOutput);
        // 口径（用户 2026-09-19 决策）：对外报告的占用率以**模型配置的上下文窗口**为分母，
        // 与 UI 面板/用户认知一致，不再被「预留输出」静默缩小分母。
        var usageRatio = normalizedUsedTokens / (double)modelWindow;
        // 门禁仍按**有效输入窗口**判定：0.60/0.75/TriggerRatio/0.92 这组阈值的语义是
        // 「输入还剩多少」，若改用模型窗口作分母，触发点会被推后到超出 provider 输入上限。
        var gateRatio = normalizedUsedTokens / (double)effectiveWindow;
        var state = gateRatio switch
        {
            >= ContextHealthGateThresholds.BlockingRatio => ContextHealthState.Blocking,
            _ => ContextHealthState.Healthy,
        };
        if (state == ContextHealthState.Healthy)
        {
            if (gateRatio >= effectiveThreshold)
                state = ContextHealthState.Critical;
            else if (gateRatio >= ContextHealthGateThresholds.UnhealthyRatio)
                state = ContextHealthState.Unhealthy;
            else if (gateRatio >= ContextHealthGateThresholds.WarningRatio)
                state = ContextHealthState.Warning;
        }

        return new ContextHealthSnapshot(
            sessionId,
            normalizedUsedTokens,
            modelWindow,
            effectiveWindow,
            remaining,
            usageRatio,
            state,
            state >= ContextHealthState.Warning,
            state is ContextHealthState.Critical or ContextHealthState.Blocking,
            state == ContextHealthState.Blocking,
            // 2026-09-22 事故可见性：把门禁比率（分母=有效输入窗口）一并报告出去。
            // 旧快照只暴露 UsageRatio（分母=模型窗口），本次事故中 usageRatio=0.609 看似宽松，
            // 而 gateRatio=1.0041 已经超限，观测盲点就在于这个比率没有出口。
            gateRatio)
        {
            // 阈值常量随快照输出，供诊断直接归因；Trigger 回写本次实际生效的压缩触发阈值
            // （可能来自 AutoCompactionThreshold 配置，未必等于默认 0.80）。
            GateThresholds = ContextHealthThresholds.Default with { Trigger = effectiveThreshold },
            // 容量分量与来源（方案 §2.5）：让 UI 能按来源给「不可用于输入」的那段命名，
            // 而不是用 windowLimit − effectiveLimit 猜成「预留输出」。
            ProviderInputLimitTokens = providerInputLimitKnown ? providerInputLimit : null,
            RequestedOutputBudgetTokens = reportedOutputBudget,
            SafetyBufferTokens = safetyBuffer,
            EffectiveWindowSource = effectiveWindowSource,
        };
    }

    /// <summary>
    /// 判定有效输入上限的受限来源。Provider 输入上限优先判定（它一定来自真实配置）；
    /// 否则按输出预算与安全余量的组合命名；两者都没有 ⇒ 有效上限就是模型窗口；
    /// 只有回退输出预算参与时明确记为 fallback，不冒充用户预算。
    /// </summary>
    private static string ResolveEffectiveWindowSource(
        bool providerConstrained,
        int? reportedOutputBudget,
        int safetyBuffer,
        int reservedOutput)
    {
        if (providerConstrained)
            return ContextEffectiveWindowSources.ProviderInputLimit;

        var outputReserveApplied = reservedOutput > 0;
        var realOutputBudget = reportedOutputBudget is > 0 && outputReserveApplied;
        if (outputReserveApplied && !realOutputBudget)
        {
            // 门禁用的是内置回退预留（容量没给实际输出预算）：无论是否叠加安全余量，
            // 都不能声称这是用户配置的「预留输出」。
            return ContextEffectiveWindowSources.FallbackOutputReserve;
        }

        if (realOutputBudget && safetyBuffer > 0)
            return ContextEffectiveWindowSources.OutputReserveAndSafetyMargin;
        if (realOutputBudget)
            return ContextEffectiveWindowSources.OutputReserve;
        if (safetyBuffer > 0)
            return ContextEffectiveWindowSources.SafetyMargin;
        return ContextEffectiveWindowSources.ModelWindow;
    }

    /// <summary>
    /// Estimates how many more messages can fit before triggering each health threshold.
    /// </summary>
    /// <param name="usedTokens">Current token usage.</param>
    /// <param name="contextWindowTokens">Model context window size.</param>
    /// <param name="avgMessageTokens">Average tokens per message (~2500 default).</param>
    public CapacityPrediction PredictCapacity(
        int usedTokens,
        int contextWindowTokens,
        int avgMessageTokens = 2500)
    {
        var modelWindow = Math.Max(1, contextWindowTokens);
        var remaining = Math.Max(0, modelWindow - Math.Max(0, usedTokens));

        int MsgsUntil(double threshold)
        {
            var target = (int)(modelWindow * threshold);
            var gap = target - Math.Max(0, usedTokens);
            return gap <= 0 ? 0 : (int)Math.Ceiling(gap / (double)Math.Max(1, avgMessageTokens));
        }

        return new CapacityPrediction(
            UsedTokens: Math.Max(0, usedTokens),
            ModelWindow: modelWindow,
            RemainingTokens: remaining,
            EstimatedMessagesUntilWarning: MsgsUntil(0.60),
            EstimatedMessagesUntilCritical: MsgsUntil(ContextCompactionDefaults.TriggerRatio),
            EstimatedMessagesUntilBlocking: MsgsUntil(0.92),
            AverageMessageTokens: avgMessageTokens);
    }
}
