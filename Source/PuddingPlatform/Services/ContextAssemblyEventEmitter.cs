using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services;

/// <summary>
/// IContextAssemblyEventEmitter 实现：将模型实际所见的 context 各层正文（脱敏后）写入 canonical
/// Conversation Event Store。SSE 只是 Event Store 的可恢复投递视图。
/// </summary>
public sealed class ContextAssemblyEventEmitter : IContextAssemblyEventEmitter
{
    /// <summary>P0-4f-1a step6: context.assembled 事件的固定 producer_component（runtime 域 context_assembly 子系统）。</summary>
    private const string ContextAssemblyProducerComponent = "runtime.context_assembly";

    private readonly IConversationEventStore _eventStore;
    private readonly ILogger<ContextAssemblyEventEmitter> _logger;

    public ContextAssemblyEventEmitter(
        IConversationEventStore eventStore,
        ILogger<ContextAssemblyEventEmitter> logger)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    public async Task EmitAsync(
        string sessionId,
        string workspaceId,
        string? agentId,
        string? turnId,
        string? traceId,
        IReadOnlyList<ContextAssemblyLayerEmission> layers,
        string assembledAtIso,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                sessionId,
                agentId,
                turnId,
                assembledAt = assembledAtIso,
                layers = layers.Select(l => new
                {
                    name = l.Name,
                    contentHash = l.ContentHash,
                    content = l.Content,
                    truncated = l.Truncated,
                }).ToArray(),
            };
            var element = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var eventId = $"context:assembled:{Guid.NewGuid():N}";
            await _eventStore.AppendAsync(
                sessionId,
                expectedVersion: -1,
                [
                    new NewConversationEvent(
                        eventId,
                        ConversationEventTypes.ContextAssembled,
                        SchemaVersion: 1,
                        WorkspaceId: workspaceId,
                        TurnId: turnId,
                        CommandId: null,
                        RunId: null,
                        MessageId: null,
                        CorrelationId: null,
                        CausationId: null,
                        ProducerEventId: null,
                        Payload: element,
                        AgentId: agentId,
                        SourceKind: ConversationEventSourceKind.Agent,
                        TraceId: traceId,
                        ProducerComponent: ContextAssemblyProducerComponent),
                ],
                EventWriteCondition.ForRun(
                    $"context-assembly:{sessionId}",
                    0),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextAssemblyEmitter] Failed to persist context.assembled session={Session} agent={Agent}",
                sessionId, agentId);
        }
    }
}
