using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Controllers.Api;

[Authorize]
[ApiController]
[Route("api/debug/subconscious")]
public sealed class SubconsciousDebugApiController : ControllerBase
{
    private readonly ISubconsciousRuntimeControl _runtimeControl;
    private readonly ISubconsciousJobQueue _jobQueue;
    private readonly IHookPublisher _hookPublisher;
    private readonly IOptions<SubconsciousOptions> _options;

    public SubconsciousDebugApiController(
        ISubconsciousRuntimeControl runtimeControl,
        ISubconsciousJobQueue jobQueue,
        IHookPublisher hookPublisher,
        IOptions<SubconsciousOptions> options)
    {
        _runtimeControl = runtimeControl;
        _jobQueue = jobQueue;
        _hookPublisher = hookPublisher;
        _options = options;
    }

    [HttpGet("debug")]
    public async Task<ActionResult<SubconsciousRuntimeControlSnapshot>> GetDebug(CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        var snapshot = await _runtimeControl.GetSnapshotAsync(ct);
        return Ok(snapshot);
    }

    [HttpPost("start")]
    public async Task<ActionResult<SubconsciousRuntimeControlSnapshot>> Start(
        [FromBody] SubconsciousRuntimeControlRequest? request,
        CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        var snapshot = await _runtimeControl.StartAsync(
            request ?? new SubconsciousRuntimeControlRequest(),
            ct);
        return Ok(snapshot);
    }

    [HttpPost("stop")]
    public async Task<ActionResult<SubconsciousRuntimeControlSnapshot>> Stop(
        [FromBody] SubconsciousRuntimeControlRequest? request,
        CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        var snapshot = await _runtimeControl.StopAsync(
            request ?? new SubconsciousRuntimeControlRequest(),
            ct);
        return Ok(snapshot);
    }

    [HttpPost("trigger")]
    public async Task<ActionResult<SubconsciousDebugTriggerResponse>> Trigger(
        [FromBody] SubconsciousDebugTriggerRequest request,
        CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.AgentId)
            || string.IsNullOrWhiteSpace(request.AgentTemplateId))
        {
            return BadRequest(new
            {
                error = "sessionId, workspaceId, agentId and agentTemplateId are required.",
            });
        }

        var sourceEventId = string.IsNullOrWhiteSpace(request.SourceEventId)
            ? $"debug-{Guid.NewGuid():N}"
            : request.SourceEventId.Trim();
        var sourceCompactionId = string.IsNullOrWhiteSpace(request.SourceCompactionId)
            ? $"debug-{request.SessionId.Trim()}"
            : request.SourceCompactionId.Trim();
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"debug:subconscious:{request.WorkspaceId.Trim()}:{request.SessionId.Trim()}:{sourceCompactionId}"
            : request.IdempotencyKey.Trim();

        var queueItem = await _jobQueue.EnqueueAsync(
            new SubconsciousJobEnqueueRequest
            {
                JobType = SubconsciousJobTypes.MemoryConsolidateSession,
                IdempotencyKey = idempotencyKey,
                SourceHookName = "debug.subconscious.trigger",
                SourceEventId = sourceEventId,
                SourceCompactionId = sourceCompactionId,
                Job = new ConsolidationJob
                {
                    SessionId = request.SessionId.Trim(),
                    WorkspaceId = request.WorkspaceId.Trim(),
                    AgentId = request.AgentId.Trim(),
                    AgentTemplateId = request.AgentTemplateId.Trim(),
                    LastUserMessage = string.IsNullOrWhiteSpace(request.LastUserMessage)
                        ? null
                        : request.LastUserMessage.Trim(),
                    LastAssistantReply = string.IsNullOrWhiteSpace(request.LastAssistantReply)
                        ? null
                        : request.LastAssistantReply.Trim(),
                },
            },
            ct);

        return Ok(new SubconsciousDebugTriggerResponse
        {
            JobId = queueItem.JobId,
            Status = queueItem.Status,
            JobType = queueItem.JobType,
            IdempotencyKey = queueItem.IdempotencyKey,
            WorkspaceId = queueItem.Job.WorkspaceId,
            SessionId = queueItem.Job.SessionId,
            AgentId = queueItem.Job.AgentId,
            AgentTemplateId = queueItem.Job.AgentTemplateId,
            SourceEventId = queueItem.SourceEventId,
            SourceCompactionId = queueItem.SourceCompactionId,
        });
    }

    [HttpPost("hooks/session-compressed")]
    public async Task<ActionResult<SubconsciousDebugHookTriggerResponse>> TriggerSessionCompressedHook(
        [FromBody] SubconsciousDebugSessionCompressedHookRequest request,
        CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.OriginalSessionId)
            || string.IsNullOrWhiteSpace(request.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.AgentId)
            || string.IsNullOrWhiteSpace(request.AgentTemplateId))
        {
            return BadRequest(new
            {
                error = "originalSessionId, workspaceId, agentId and agentTemplateId are required.",
            });
        }

        var workspaceId = request.WorkspaceId.Trim();
        var originalSessionId = request.OriginalSessionId.Trim();
        var agentId = request.AgentId.Trim();
        var agentTemplateId = request.AgentTemplateId.Trim();
        var compactionId = string.IsNullOrWhiteSpace(request.CompactionId)
            ? $"debug-hook-{Guid.NewGuid():N}"
            : request.CompactionId.Trim();

        var payload = new SessionCompressedHookPayload
        {
            WorkspaceId = workspaceId,
            OriginalSessionId = originalSessionId,
            NewSessionId = string.IsNullOrWhiteSpace(request.NewSessionId) ? null : request.NewSessionId.Trim(),
            AgentId = agentId,
            AgentTemplateId = agentTemplateId,
            CompactionId = compactionId,
            Mode = "Debug",
            Level = "Full",
            Reason = string.IsNullOrWhiteSpace(request.Reason)
                ? "subconscious debug hook trigger"
                : request.Reason.Trim(),
            SummaryPreview = string.IsNullOrWhiteSpace(request.SummaryPreview)
                ? null
                : request.SummaryPreview.Trim(),
        };

        var eventId = await _hookPublisher.PublishAsync(
            HookEventNames.SessionCompressed,
            payload,
            new HookPublishOptions
            {
                WorkspaceId = workspaceId,
                SessionId = originalSessionId,
                AgentId = agentId,
                SourceType = "debug",
                SourceId = "subconscious_debug.session_compressed",
                IdempotencyKey = $"debug:hook:session.compressed:{workspaceId}:{originalSessionId}:{compactionId}",
            },
            ct);

        return Ok(new SubconsciousDebugHookTriggerResponse
        {
            EventId = eventId,
            SourceHookName = HookEventNames.SessionCompressed.Value,
            SourceCompactionId = compactionId,
            WorkspaceId = workspaceId,
            SessionId = originalSessionId,
            AgentId = agentId,
            AgentTemplateId = agentTemplateId,
        });
    }

    [HttpGet("jobs/lookup")]
    public async Task<ActionResult<SubconsciousJobQueueItem>> LookupJob(
        [FromQuery] string? jobId,
        [FromQuery] string? idempotencyKey,
        [FromQuery] string? sourceHookName,
        [FromQuery] string? sourceCompactionId,
        [FromQuery] string? workspaceId,
        [FromQuery] string? sessionId,
        CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        if (string.IsNullOrWhiteSpace(jobId)
            && string.IsNullOrWhiteSpace(idempotencyKey)
            && string.IsNullOrWhiteSpace(sourceCompactionId))
        {
            return BadRequest(new
            {
                error = "jobId, idempotencyKey or sourceCompactionId is required.",
            });
        }

        var item = await _jobQueue.FindLatestAsync(
            new SubconsciousJobLookupQuery
            {
                JobId = jobId,
                IdempotencyKey = idempotencyKey,
                SourceHookName = sourceHookName,
                SourceCompactionId = sourceCompactionId,
                WorkspaceId = workspaceId,
                SessionId = sessionId,
            },
            ct);

        if (item is null)
            return NotFound();

        return Ok(item);
    }

    [HttpGet("jobs/{jobId}/result")]
    public async Task<ActionResult<SubconsciousJobResultEnvelope>> GetJobResult(
        string jobId,
        CancellationToken ct)
    {
        if (!_options.Value.DebugApiEnabled)
            return NotFound();

        var result = await _jobQueue.GetResultAsync(jobId, ct);
        if (result is null)
            return NotFound();

        return Ok(result);
    }
}

public sealed record SubconsciousDebugTriggerRequest
{
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? LastUserMessage { get; init; }
    public string? LastAssistantReply { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceCompactionId { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed record SubconsciousDebugTriggerResponse
{
    public required string JobId { get; init; }
    public required string Status { get; init; }
    public required string JobType { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceCompactionId { get; init; }
}

public sealed record SubconsciousDebugSessionCompressedHookRequest
{
    public string? WorkspaceId { get; init; }
    public string? OriginalSessionId { get; init; }
    public string? NewSessionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? CompactionId { get; init; }
    public string? Reason { get; init; }
    public string? SummaryPreview { get; init; }
}

public sealed record SubconsciousDebugHookTriggerResponse
{
    public required string EventId { get; init; }
    public required string SourceHookName { get; init; }
    public required string SourceCompactionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentTemplateId { get; init; }
}
