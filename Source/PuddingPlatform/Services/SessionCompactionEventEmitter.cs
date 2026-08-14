using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services;

/// <summary>
/// ISessionCompactionEventEmitter 实现：将自动压缩生命周期写入 canonical
/// Conversation Event Store。SSE 只是 Event Store 的可恢复投递视图。
/// </summary>
public sealed class SessionCompactionEventEmitter : ISessionCompactionEventEmitter
{
    /// <summary>P0-4f-1a step6: context.compaction.* 事件的固定 producer_component（runtime 域 compaction 子系统）。</summary>
    private const string CompactionProducerComponent = "runtime.compaction";

    private readonly IConversationEventStore _eventStore;
    private readonly ILogger<SessionCompactionEventEmitter> _logger;

    public SessionCompactionEventEmitter(
        IConversationEventStore eventStore,
        ILogger<SessionCompactionEventEmitter> logger)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

        public async Task EmitAsync(
        string sessionId,
        string workspaceId,
        string eventType,
        object payload,
        string? traceId,
        CancellationToken ct = default)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var compactionId =
                element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("compactionId", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
            var eventId =
                $"compaction:{compactionId ?? Guid.NewGuid().ToString("N")}:{eventType}:{Guid.NewGuid():N}";
            await _eventStore.AppendAsync(
                sessionId,
                expectedVersion: -1,
                [
                    new NewConversationEvent(
                        eventId,
                        eventType,
                        SchemaVersion: 1,
                        WorkspaceId: workspaceId,
                        TurnId: null,
                        CommandId: compactionId,
                        RunId: null,
                        MessageId: null,
                        CorrelationId: compactionId,
                        CausationId: null,
                        ProducerEventId: null,
                        Payload: element,
                        TraceId: traceId,
                        ProducerComponent: CompactionProducerComponent),
                ],
                EventWriteCondition.ForRun(
                    $"auto-compaction:{compactionId ?? sessionId}",
                    0),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CompactionEmitter] Failed to persist {EventType} session={Session}",
                eventType, sessionId);
        }
    }
}
