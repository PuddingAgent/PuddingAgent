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

        if (string.IsNullOrWhiteSpace(payload.AgentId)
            || string.IsNullOrWhiteSpace(payload.AgentTemplateId))
        {
            _logger.LogDebug(
                "[HookSystem] Skip session.compressed memory maintenance because agent identity is missing compactionId={CompactionId}",
                payload.CompactionId);
            return;
        }

        var workspaceId = string.IsNullOrWhiteSpace(payload.WorkspaceId) ? evt.WorkspaceId : payload.WorkspaceId;
        var job = new ConsolidationJob
        {
            SessionId = payload.OriginalSessionId,
            WorkspaceId = workspaceId,
            AgentId = payload.AgentId,
            AgentTemplateId = payload.AgentTemplateId,
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
