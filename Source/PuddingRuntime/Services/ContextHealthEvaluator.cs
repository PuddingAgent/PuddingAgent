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
        int? maxInputTokens = null)
    {
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
        var ratio = normalizedUsedTokens / (double)effectiveWindow;
        var state = ratio switch
        {
            >= 0.92 => ContextHealthState.Blocking,
            >= 0.80 => ContextHealthState.Critical,
            >= 0.75 => ContextHealthState.Unhealthy,
            >= 0.60 => ContextHealthState.Warning,
            _ => ContextHealthState.Healthy,
        };

        return new ContextHealthSnapshot(
            sessionId,
            normalizedUsedTokens,
            modelWindow,
            effectiveWindow,
            remaining,
            ratio,
            state,
            state >= ContextHealthState.Warning,
            state is ContextHealthState.Critical or ContextHealthState.Blocking,
            state == ContextHealthState.Blocking);
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
            EstimatedMessagesUntilCritical: MsgsUntil(0.80),
            EstimatedMessagesUntilBlocking: MsgsUntil(0.92),
            AverageMessageTokens: avgMessageTokens);
    }
}
