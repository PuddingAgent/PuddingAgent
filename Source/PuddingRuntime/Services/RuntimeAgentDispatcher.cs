using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

public sealed class RuntimeAgentDispatcher : IRuntimeAgentDispatcher
{
    private readonly AgentExecutionService _executionService;

    public RuntimeAgentDispatcher(AgentExecutionService executionService)
    {
        _executionService = executionService;
    }

    public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
        => _executionService.ExecuteAsync(request, ct);
}
