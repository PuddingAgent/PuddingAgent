using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

public sealed class ContextHealthEvaluator
{
    public ContextHealthSnapshot Evaluate(
        string sessionId,
        int usedTokens,
        int contextWindowTokens,
        int maxOutputTokens,
        int safetyBufferTokens = 0)
    {
        // Main context health is intentionally based on the model's advertised
        // context window. UI and auto-compaction need the current session's
        // remaining window, not a hidden derived budget after output reservation.
        var modelWindow = Math.Max(1, contextWindowTokens);
        var remaining = Math.Max(0, modelWindow - Math.Max(0, usedTokens));
        var ratio = Math.Max(0, usedTokens) / (double)modelWindow;
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
            Math.Max(0, usedTokens),
            Math.Max(0, contextWindowTokens),
            modelWindow,
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
