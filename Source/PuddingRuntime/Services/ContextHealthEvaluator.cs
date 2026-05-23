using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

public sealed class ContextHealthEvaluator
{
    private const int DefaultSafetyBufferTokens = 3_000;
    private const int DefaultMaxReservedOutputTokens = 20_000;

    public ContextHealthSnapshot Evaluate(
        string sessionId,
        int usedTokens,
        int contextWindowTokens,
        int maxOutputTokens,
        int safetyBufferTokens = DefaultSafetyBufferTokens)
    {
        var reservedOutputTokens = Math.Min(
            Math.Max(0, maxOutputTokens),
            DefaultMaxReservedOutputTokens);
        var effectiveWindow = Math.Max(
            1,
            contextWindowTokens - reservedOutputTokens - Math.Max(0, safetyBufferTokens));
        var remaining = Math.Max(0, effectiveWindow - Math.Max(0, usedTokens));
        var ratio = Math.Max(0, usedTokens) / (double)effectiveWindow;
        var state = ratio switch
        {
            >= 0.92 => ContextHealthState.Blocking,
            >= 0.85 => ContextHealthState.Critical,
            >= 0.75 => ContextHealthState.Unhealthy,
            >= 0.60 => ContextHealthState.Warning,
            _ => ContextHealthState.Healthy,
        };

        return new ContextHealthSnapshot(
            sessionId,
            Math.Max(0, usedTokens),
            Math.Max(0, contextWindowTokens),
            effectiveWindow,
            remaining,
            ratio,
            state,
            state >= ContextHealthState.Warning,
            state is ContextHealthState.Critical or ContextHealthState.Blocking,
            state == ContextHealthState.Blocking);
    }
}
