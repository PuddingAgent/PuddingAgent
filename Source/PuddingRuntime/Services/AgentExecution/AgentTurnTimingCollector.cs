using System.Diagnostics;
using PuddingCode.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// Collects the per-turn timing checkpoints the diagnostics UI needs.
/// <para>
/// Two clocks are reported deliberately and never mixed:
/// local checkpoints are relative to turn submit, while provider TTFT is relative to the
/// actual HTTP dispatch observed by the gateway. A value that was never observed stays
/// null so consumers can render "not collected" instead of a fake zero.
/// </para>
/// </summary>
internal sealed class AgentTurnTimingCollector
{
    private readonly long _turnStartedAt = Stopwatch.GetTimestamp();
    private long _modelCallStartedAt;
    private long _modelTotalMs;
    private long _toolTotalMs;
    private bool _contentFrameObserved;

    /// <summary>Wall-clock start of this turn (used for cross-process correlation only).</summary>
    public DateTimeOffset TurnStartedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Duration of loading/persisting the session history before the loop.</summary>
    public long? HistoryLoadMs { get; set; }

    /// <summary>Duration of the Agent context pipeline assembly (static/tools/skills/memory layers).</summary>
    public long? ContextAssembleMs { get; set; }

    /// <summary>Per-stage context pipeline durations (stage → ms), when the pipeline reported them.</summary>
    public IReadOnlyDictionary<string, long>? ContextStagesMs { get; set; }

    /// <summary>Duration of resolving the effective LLM configuration.</summary>
    public long? LlmConfigResolveMs { get; set; }

    /// <summary>Duration of building the tool specification sent to the provider.</summary>
    public long? ToolBuildMs { get; set; }

    /// <summary>Elapsed ms when the first context frame was ready to be streamed.</summary>
    public long? ContextReadyMs { get; private set; }

    /// <summary>Elapsed ms when the first provider call was handed to the LLM invocation layer.</summary>
    public long? ModelDispatchMs { get; private set; }

    /// <summary>Provider-reported time from HTTP dispatch to response headers (first call with data).</summary>
    public long? ProviderHeadersMs { get; private set; }

    /// <summary>Provider TTFT: HTTP dispatch to first non-empty model delta (reasoning/content/tool).</summary>
    public long? ProviderTtftMs { get; private set; }

    /// <summary>How <see cref="ProviderTtftMs"/> was measured: provider dispatch clock or local fallback.</summary>
    public string? ProviderTtftSource { get; private set; }

    /// <summary>Provider-relative time to the first reasoning delta.</summary>
    public long? ProviderFirstReasoningMs { get; private set; }

    /// <summary>Provider-relative time to the first content delta.</summary>
    public long? ProviderFirstContentMs { get; private set; }

    /// <summary>Provider-relative time to the first tool-call delta.</summary>
    public long? ProviderFirstToolDeltaMs { get; private set; }

    /// <summary>End-to-end (turn submit) time until the first assistant content frame was emitted.</summary>
    public long? FirstContentFrameMs { get; private set; }

    /// <summary>Total time spent inside provider streams (all rounds).</summary>
    public long ModelMs => _modelTotalMs;

    /// <summary>Total time spent executing tools (all calls).</summary>
    public long ToolMs => _toolTotalMs;

    /// <summary>Provider stream invocations in this turn.</summary>
    public int ModelCalls { get; private set; }

    /// <summary>Tool invocations counted by the caller.</summary>
    public int ToolCalls { get; private set; }

    /// <summary>Elapsed ms when the turn reached its terminal state.</summary>
    public long? CompletedMs { get; private set; }

    /// <summary>Current elapsed ms since turn submit.</summary>
    public long ElapsedMs => Elapsed(_turnStartedAt, Stopwatch.GetTimestamp());

    public void MarkContextReady() => ContextReadyMs ??= ElapsedMs;

    /// <summary>Start a provider stream call and remember the local dispatch instant.</summary>
    public void BeginModelCall()
    {
        _modelCallStartedAt = Stopwatch.GetTimestamp();
        ModelCalls++;
        ModelDispatchMs ??= ElapsedMs;
    }

    /// <summary>Finish the current provider stream call and accumulate its wall duration.</summary>
    public void EndModelCall()
    {
        if (_modelCallStartedAt == 0)
            return;

        _modelTotalMs += Elapsed(_modelCallStartedAt, Stopwatch.GetTimestamp());
        _modelCallStartedAt = 0;
    }

    /// <summary>
    /// Observe a provider delta. Provider-relative fields come from the gateway; when a
    /// gateway does not report them, the local model-call clock is used as a clearly
    /// attributable fallback (it then includes request build and transport).
    /// Returns true exactly once: when provider TTFT became known.
    /// </summary>
    public bool ObserveProviderDelta(StreamDelta delta)
    {
        if (ProviderHeadersMs is null && delta.ProviderHeadersMs is { } headersMs)
            ProviderHeadersMs = headersMs;

        var providerRelative = delta.ProviderDispatchElapsedMs
            ?? (_modelCallStartedAt == 0 ? null : Elapsed(_modelCallStartedAt, Stopwatch.GetTimestamp()));
        var hasReasoning = !string.IsNullOrEmpty(delta.ReasoningDelta);
        var hasContent = !string.IsNullOrEmpty(delta.ContentDelta);
        var hasToolDelta = delta.ToolCallIndex.HasValue;

        if (hasReasoning && ProviderFirstReasoningMs is null)
            ProviderFirstReasoningMs = providerRelative;
        if (hasContent && ProviderFirstContentMs is null)
            ProviderFirstContentMs = providerRelative;
        if (hasToolDelta && ProviderFirstToolDeltaMs is null)
            ProviderFirstToolDeltaMs = providerRelative;

        if (ProviderTtftMs is not null
            || providerRelative is null
            || (!hasReasoning && !hasContent && !hasToolDelta))
        {
            return false;
        }

        ProviderTtftMs = providerRelative;
        ProviderTtftSource = delta.ProviderDispatchElapsedMs is not null
            ? "provider_dispatch"
            : "local_model_call";
        return true;
    }

    /// <summary>Record the moment the first assistant content frame is emitted.</summary>
    public void MarkFirstContentFrame()
    {
        if (_contentFrameObserved)
            return;

        _contentFrameObserved = true;
        FirstContentFrameMs = ElapsedMs;
    }

    /// <summary>Register one executed tool call with its measured duration.</summary>
    public void RegisterToolCall(long durationMs)
    {
        ToolCalls++;
        if (durationMs > 0)
            _toolTotalMs += durationMs;
    }

    public void MarkCompleted() => CompletedMs ??= ElapsedMs;

    /// <summary>Build the JSON payload emitted with the terminal <c>done</c> frame.</summary>
    public IReadOnlyDictionary<string, object?> ToPayload() => new Dictionary<string, object?>
    {
        ["turnStartedAtUtc"] = TurnStartedAtUtc,
        ["historyLoadMs"] = HistoryLoadMs,
        ["contextAssembleMs"] = ContextAssembleMs,
        ["contextStagesMs"] = ContextStagesMs,
        ["llmConfigResolveMs"] = LlmConfigResolveMs,
        ["toolBuildMs"] = ToolBuildMs,
        ["contextReadyMs"] = ContextReadyMs,
        ["modelDispatchMs"] = ModelDispatchMs,
        ["providerHeadersMs"] = ProviderHeadersMs,
        ["providerTtftMs"] = ProviderTtftMs,
        ["providerTtftSource"] = ProviderTtftSource ?? "unavailable",
        ["providerFirstReasoningMs"] = ProviderFirstReasoningMs,
        ["providerFirstContentMs"] = ProviderFirstContentMs,
        ["providerFirstToolDeltaMs"] = ProviderFirstToolDeltaMs,
        ["firstContentFrameMs"] = FirstContentFrameMs,
        ["modelMs"] = ModelMs,
        ["toolMs"] = ToolMs,
        ["modelCalls"] = ModelCalls,
        ["toolCalls"] = ToolCalls,
        ["completedMs"] = CompletedMs,
    };

    private static long Elapsed(long startedAt, long endedAt)
        => (long)((endedAt - startedAt) * 1000.0 / Stopwatch.Frequency);
}
