using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Hooks;

/// <summary>
/// Bridges session compaction lifecycle hooks into the subconscious memory maintenance queue.
/// </summary>
public sealed class SessionCompressedMemoryMaintenanceHook : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IInternalEventBus _eventBus;
    private readonly ISubconsciousJobQueue _jobQueue;
    private readonly ILogger<SessionCompressedMemoryMaintenanceHook> _logger;
    private IEventSubscriptionHandle? _subscription;

    public SessionCompressedMemoryMaintenanceHook(
        IInternalEventBus eventBus,
        ISubconsciousJobQueue jobQueue,
        ILogger<SessionCompressedMemoryMaintenanceHook> logger)
    {
        _eventBus = eventBus;
        _jobQueue = jobQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await _eventBus.SubscribeAsync(
            HookEventNames.SessionCompressed.Value,
            evt => HandleAsync(evt, stoppingToken),
            stoppingToken);

        _logger.LogInformation(
            "[HookSystem] SessionCompressedMemoryMaintenanceHook subscribed event={EventType}",
            HookEventNames.SessionCompressed.Value);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (_subscription is not null)
            {
                await _eventBus.UnsubscribeAsync(_subscription);
                _subscription.Dispose();
            }
        }
    }

    public async Task HandleAsync(InternalEvent evt, CancellationToken ct = default)
    {
        if (!string.Equals(evt.Type, HookEventNames.SessionCompressed.Value, StringComparison.Ordinal))
            return;

        var payload = ResolvePayload(evt.Payload);
        if (payload is null)
        {
            _logger.LogWarning(
                "[HookSystem] Skip session.compressed memory maintenance because payload is invalid eventId={EventId}",
                evt.EventId);
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.AgentId))
        {
            _logger.LogWarning(
                "[HookSystem] Skip session.compressed memory maintenance because agent instance id is missing compactionId={CompactionId} session={SessionId}",
                payload.CompactionId,
                payload.OriginalSessionId);
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.AgentTemplateId))
        {
            // 降级入队：Worker 通过 AgentId 解析 LLM 配置，模板 ID 缺失不应静默丢弃
            // 记忆维护任务（修复自动压缩静默丢失整合任务的问题）。
            _logger.LogWarning(
                "[HookSystem] session.compressed payload missing AgentTemplateId; enqueueing with empty template compactionId={CompactionId} session={SessionId} agent={AgentId}",
                payload.CompactionId,
                payload.OriginalSessionId,
                payload.AgentId);
        }

        var workspaceId = string.IsNullOrWhiteSpace(payload.WorkspaceId) ? evt.WorkspaceId : payload.WorkspaceId;
        var job = new ConsolidationJob
        {
            SessionId = payload.OriginalSessionId,
            WorkspaceId = workspaceId,
            AgentId = payload.AgentId,
            AgentTemplateId = payload.AgentTemplateId ?? string.Empty,
            LastAssistantReply = payload.SummaryPreview,
            MemoryNotes = payload.MemoryNotes,
        };

        var queueItem = await _jobQueue.EnqueueAsync(new SubconsciousJobEnqueueRequest
        {
            JobType = SubconsciousJobTypes.MemoryConsolidateSession,
            IdempotencyKey = BuildIdempotencyKey(workspaceId, payload),
            SourceHookName = HookEventNames.SessionCompressed.Value,
            SourceEventId = evt.EventId,
            SourceCompactionId = payload.CompactionId,
            Job = job,
        }, ct);

        _logger.LogInformation(
            "[HookSystem] Enqueued durable memory maintenance job from session.compressed jobId={JobId} status={Status} compactionId={CompactionId} session={SessionId}",
            queueItem.JobId,
            queueItem.Status,
            payload.CompactionId,
            payload.OriginalSessionId);
    }

    private static string BuildIdempotencyKey(string workspaceId, SessionCompressedHookPayload payload)
        => $"{SubconsciousJobTypes.MemoryConsolidateSession}:{workspaceId}:{payload.OriginalSessionId}:{payload.CompactionId}";

    private static SessionCompressedHookPayload? ResolvePayload(object? payload)
    {
        return payload switch
        {
            SessionCompressedHookPayload typed => typed,
            JsonElement element => element.Deserialize<SessionCompressedHookPayload>(JsonOptions),
            _ => null,
        };
    }
}
