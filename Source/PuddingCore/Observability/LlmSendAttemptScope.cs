namespace PuddingCode.Observability;

/// <summary>
/// CORE-I1-06: one real provider send (HTTP request / stream creation). A retry or a rebuilt Responses file request is a
/// new send and therefore a new attempt; the observer is the only component allowed to open a budget attempt per send.
/// </summary>
public sealed record LlmSendAttemptRequest(
    string ModelId,
    string ModelRevision,
    string PriceVersion,
    string CostUnit,
    long MaxInputTokens,
    long MaxOutputTokens,
    DateTimeOffset DispatchAt);

/// <summary>A send that was admitted. <see cref="Gated"/> is false only for Legacy roots, which are diagnostic.</summary>
public sealed record LlmSendPermit(string AttemptId, long MaxOutputTokens, bool Gated)
{
    public static LlmSendPermit Ungated(string attemptId) => new(attemptId, 0, false);
}

/// <summary>Usage observed by the transport for exactly one send; null fields mean "not reported".</summary>
public sealed record LlmSendUsage(long? InputTokens, long? OutputTokens, long? CacheHitTokens = null);

/// <summary>
/// Transport-level attempt observer. Implementations live in the runtime adapter and use the host-bound budget port;
/// gateways/clients only call begin/complete at actual send boundaries and must not keep a second budget state.
/// </summary>
public interface ILlmSendAttemptObserver
{
    ValueTask<LlmSendPermit?> BeforeSendAsync(LlmSendAttemptRequest request, CancellationToken ct = default);
    ValueTask CompleteAsync(LlmSendPermit? permit, LlmSendUsage? usage, string? providerReceipt,
        string? failureReason, CancellationToken ct = default);
}

/// <summary>
/// Marker for clients that implement per-send budget attempts on their buffered path; the facade then installs the
/// observer scope instead of wrapping the whole client call in one attempt.
/// </summary>
public interface IBufferedSendAttemptBudgetClient
{
}

/// <summary>Marker for clients that implement per-send budget attempts on their streaming path.</summary>
public interface IStreamingSendAttemptBudgetClient
{
}

/// <summary>
/// Marker for a gateway that performs more than one real provider send per call (e.g. Responses file rebuild) and owns
/// one budget attempt per send itself; transports must then not wrap the whole gateway call in a second attempt.
/// </summary>
public interface IPerSendBudgetGateway
{
}

/// <summary>
/// AsyncLocal carrier that binds the observer to the current logical invocation. It is set by the invocation facade
/// only for budgeted managed calls; transport code reads it at each actual send and clears it when the invocation ends.
/// </summary>
public static class LlmSendAttemptScope
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    public static ILlmSendAttemptObserver? Current => CurrentState.Value?.Observer;

    public static IDisposable Push(ILlmSendAttemptObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var previous = CurrentState.Value;
        CurrentState.Value = new State(observer);
        return new Pop(previous);
    }

    /// <summary>Called by the transport immediately before a real provider send; null when the invocation is unmanaged.</summary>
    public static ValueTask<LlmSendPermit?> BeforeSendAsync(LlmSendAttemptRequest request,
        CancellationToken ct = default)
        => Current is { } observer ? observer.BeforeSendAsync(request, ct) : ValueTask.FromResult<LlmSendPermit?>(null);

    /// <summary>Called exactly once for every permit returned by <see cref="BeforeSendAsync"/>.</summary>
    public static ValueTask CompleteAsync(LlmSendPermit? permit, LlmSendUsage? usage, string? providerReceipt,
        string? failureReason, CancellationToken ct = default)
        => Current is { } observer
            ? observer.CompleteAsync(permit, usage, providerReceipt, failureReason, ct)
            : ValueTask.CompletedTask;

    private sealed record State(ILlmSendAttemptObserver Observer);

    private sealed class Pop(State? previous) : IDisposable
    {
        private State? _previous = previous;
        public void Dispose()
        {
            CurrentState.Value = _previous;
            _previous = null;
        }
    }
}
