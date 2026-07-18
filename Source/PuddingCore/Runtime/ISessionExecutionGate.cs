namespace PuddingCode.Runtime;

/// <summary>
/// Serializes all Runtime executions that mutate the same session.
/// Conversation workers, durable message delivery, heartbeat and direct
/// Runtime dispatch must all cross this boundary before touching session state.
/// </summary>
public interface ISessionExecutionGate
{
    ValueTask<IAsyncDisposable> EnterAsync(
        string sessionId,
        string executionSource,
        CancellationToken ct = default);
}
