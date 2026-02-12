using System.Runtime.CompilerServices;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PuddingRuntime.Services;

public sealed class RuntimeAgentDispatcher : IRuntimeAgentDispatcher
{
    private readonly AgentExecutionService _executionService;
    private readonly IAgentExecutionStateRegistry _executionStateRegistry;
    private readonly IInternalEventBus? _eventBus;
    private readonly ILogger<RuntimeAgentDispatcher> _logger;

    public RuntimeAgentDispatcher(
        AgentExecutionService executionService,
        IAgentExecutionStateRegistry executionStateRegistry,
        IInternalEventBus? eventBus = null,
        ILogger<RuntimeAgentDispatcher>? logger = null)
    {
        _executionService = executionService;
        _executionStateRegistry = executionStateRegistry;
        _eventBus = eventBus;
        _logger = logger ?? NullLogger<RuntimeAgentDispatcher>.Instance;
    }

    public async Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
    {
        var agentId = ResolveAgentId(request);
        var executionId = ResolveExecutionId(request);
        var currentTask = ResolveCurrentTask(request);

        if (!_executionStateRegistry.TryBegin(request.WorkspaceId, agentId, executionId, currentTask))
        {
            var availability = _executionStateRegistry.Get(request.WorkspaceId, agentId);
            return new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = agentId,
                IsSuccess = false,
                ErrorMessage = $"Agent '{agentId}' is {availability.Status}.",
                ExecutionState = AgentExecutionState.Busy,
            };
        }

        await PublishAvailabilityChangedAsync(request, agentId, "busy", executionId, currentTask, ct);

        try
        {
            return await _executionService.ExecuteAsync(request, ct);
        }
        finally
        {
            if (_executionStateRegistry.Complete(request.WorkspaceId, agentId, executionId))
                await PublishAvailabilityChangedAsync(request, agentId, "idle", null, null, CancellationToken.None);
        }
    }

    public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
        RuntimeDispatchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var agentId = ResolveAgentId(request);
        var executionId = ResolveExecutionId(request);
        var currentTask = ResolveCurrentTask(request);

        if (!_executionStateRegistry.TryBegin(request.WorkspaceId, agentId, executionId, currentTask))
        {
            var availability = _executionStateRegistry.Get(request.WorkspaceId, agentId);
            yield return ServerSentEventFrame.Json("error", new
            {
                error = $"Agent '{agentId}' is {availability.Status}.",
                executionState = AgentExecutionState.Busy.ToString(),
            });
            yield break;
        }

        await PublishAvailabilityChangedAsync(request, agentId, "busy", executionId, currentTask, ct);

        try
        {
            await foreach (var frame in _executionService.ExecuteStreamAsync(request, ct))
                yield return frame;
        }
        finally
        {
            if (_executionStateRegistry.Complete(request.WorkspaceId, agentId, executionId))
                await PublishAvailabilityChangedAsync(request, agentId, "idle", null, null, CancellationToken.None);
        }
    }

    private async Task PublishAvailabilityChangedAsync(
        RuntimeDispatchRequest request,
        string agentId,
        string status,
        string? executionId,
        string? currentTask,
        CancellationToken ct)
    {
        if (_eventBus is null)
            return;

        try
        {
            await _eventBus.PublishAsync(new InternalEvent
            {
                Type = "agent.availability.changed",
                WorkspaceId = request.WorkspaceId,
                AgentId = agentId,
                SessionId = request.SessionId,
                Source = new EventSource { SourceType = "runtime", SourceId = agentId },
                Payload = new AgentAvailabilityChangedEventPayload
                {
                    WorkspaceId = request.WorkspaceId,
                    AgentId = agentId,
                    Status = status,
                    CurrentExecutionId = executionId,
                    CurrentTask = currentTask,
                },
                Metadata = new Dictionary<string, string>
                {
                    ["message_id"] = request.MessageId ?? string.Empty,
                    ["execution_id"] = executionId ?? string.Empty,
                },
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[RuntimeAgentDispatcher] Failed to publish availability status={Status} workspace={WorkspaceId} agent={AgentId}",
                status,
                request.WorkspaceId,
                agentId);
        }
    }

    private static string ResolveAgentId(RuntimeDispatchRequest request) =>
        !string.IsNullOrWhiteSpace(request.AgentInstanceId)
            ? request.AgentInstanceId
            : request.AgentTemplateId;

    private static string ResolveExecutionId(RuntimeDispatchRequest request) =>
        !string.IsNullOrWhiteSpace(request.MessageId)
            ? $"msg-{request.MessageId}"
            : $"session-{request.SessionId}";

    private static string? ResolveCurrentTask(RuntimeDispatchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AssignedObjective))
            return request.AssignedObjective;

        if (!string.IsNullOrWhiteSpace(request.MessageText))
            return request.MessageText.Length <= 120 ? request.MessageText : request.MessageText[..120];

        return null;
    }
}
