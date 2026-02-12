using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// Dispatches an agent execution request through the runtime.
/// </summary>
public interface IRuntimeAgentDispatcher
{
    Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default);

    IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
        RuntimeDispatchRequest request,
        CancellationToken ct = default);
}
