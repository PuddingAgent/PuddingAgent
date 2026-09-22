using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

public sealed class ContextHealthEvaluator
{
        public ContextHealthSnapshot Evaluate(
        string sessionId,
        int usedTokens,
        int contextWindowTokens,
        int maxOutputTokens,
        int safetyBufferTokens = 0,
        int? maxInputTokens = null,
        double compactionThreshold = ContextCompactionDefaults.TriggerRatio)
    {
        var effectiveThreshold = compactionThreshold is > 0 and <= 1 ? compactionThreshold : ContextCompactionDefaults.TriggerRatio;
        var modelWindow = Math.Max(1, contextWindowTokens);
        var reservedOutput = Math.Max(0, maxOutputTokens);
        var safetyBuffer = Math.Max(0, safetyBufferTokens);
        var contextDerivedInputLimit = Math.Max(1, modelWindow - reservedOutput - safetyBuffer);
        var providerInputLimit = maxInputTokens is > 0
            ? maxInputTokens.Value
            : int.MaxValue;
        var effectiveWindow = Math.Max(1, Math.Min(contextDerivedInputLimit, providerInputLimit));
        var normalizedUsedTokens = Math.Max(0, usedTokens);
        var remaining = Math.Max(0, effectiveWindow - normalizedUsedTokens);
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
        };
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
