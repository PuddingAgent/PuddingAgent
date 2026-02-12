using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Diagnostics;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 子代理运行诊断 API — 查询运行列表、详情、事件、工具审计和输出。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// ARCH-HARDEN-004：所有端点返回专用 DTO，不直接暴露 EF Entity 或匿名结构。
/// </summary>
[ApiController]
[Route("api/sub-agents/runs")]
[Authorize]
public class SubAgentRunController : ControllerBase
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ISubAgentRunStore _runStore;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SubAgentRunController(
        IDbContextFactory<PlatformDbContext> dbFactory,
        ISubAgentRunStore runStore)
    {
        _dbFactory = dbFactory;
        _runStore = runStore;
    }

    /// <summary>分页参数校验：limit 1-500，offset >= 0。</summary>
    private ActionResult? ValidatePagination(int limit, int offset)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new { message = "limit 必须在 1-500 之间" });
        if (offset < 0)
            return BadRequest(new { message = "offset 必须 >= 0" });
        return null;
    }

    /// <summary>SubAgentRunEntity → SubAgentRunSummaryDto 投影。</summary>
    private static SubAgentRunSummaryDto ToSummaryDto(SubAgentRunEntity e) => new()
    {
        RunId = e.RunId,
        ParentSessionId = e.ParentSessionId,
        SubSessionId = e.SubSessionId,
        WorkspaceId = e.WorkspaceId,
        AgentInstanceId = e.AgentInstanceId,
        TemplateId = e.TemplateId,
        Status = e.Status,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        TotalDurationMs = e.TotalDurationMs,
        TotalRounds = e.TotalRounds,
        TotalToolCalls = e.TotalToolCalls,
        ErrorMessage = e.ErrorMessage,
    };

    /// <summary>
    /// GET /api/sub-agents/runs — 分页查询子代理运行列表。
    /// 支持按 parentSessionId、workspaceId、agentInstanceId、status 过滤。
    /// 返回 PagedResultDto&lt;SubAgentRunSummaryDto&gt;。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<SubAgentRunSummaryDto>>> List(
        [FromQuery] string? parentSessionId,
        [FromQuery] string? workspaceId,
        [FromQuery] string? agentInstanceId,
        [FromQuery] string? status,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var validation = ValidatePagination(limit, offset);
        if (validation != null) return validation;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.SubAgentRuns.AsQueryable();

        if (!string.IsNullOrWhiteSpace(parentSessionId))
            query = query.Where(r => r.ParentSessionId == parentSessionId);
        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(r => r.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(r => r.AgentInstanceId == agentInstanceId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        var total = await query.CountAsync(ct);

        var entities = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var items = entities.Select(ToSummaryDto).ToList();

        return Ok(new PagedResultDto<SubAgentRunSummaryDto>
        {
            Items = items,
            Total = total,
            Offset = offset,
            Limit = limit,
        });
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId} — 获取单次运行详情。
    /// 返回 SubAgentRunDetailDto，含 Manifest 信息 + 事件/工具计数。
    /// </summary>
    [HttpGet("{runId}")]
    public async Task<ActionResult<SubAgentRunDetailDto>> Get(string runId, CancellationToken ct)
    {
        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        var m = archive.Manifest;

        var summary = new SubAgentRunSummaryDto
        {
            RunId = m.RunId,
            ParentSessionId = m.ParentSessionId,
            SubSessionId = m.SubSessionId,
            WorkspaceId = m.WorkspaceId,
            AgentInstanceId = m.AgentInstanceId,
            TemplateId = m.TemplateId,
            Status = m.Status,
            StartedAt = m.StartedAt.ToString("o"),
            CompletedAt = m.CompletedAt?.ToString("o"),
            TotalDurationMs = 0, // Manifest 无此字段，统计由 events 提供
            TotalRounds = 0,
            TotalToolCalls = 0,
            ErrorMessage = null,
        };

        return Ok(new SubAgentRunDetailDto
        {
            Summary = summary,
            Task = m.Task,
            Output = archive.Output,
            LlmProfiles = m.LlmProfiles,
            Trace = m.Trace,
            EventCount = archive.Events.Count,
            ToolCallCount = archive.Tools.Count,
        });
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/events — 获取运行事件列表（从 events.jsonl 读取）。
    /// 返回 PagedResultDto&lt;SubAgentRunEventDto&gt;，不含完整 payload。
    /// </summary>
    [HttpGet("{runId}/events")]
    public async Task<ActionResult<PagedResultDto<SubAgentRunEventDto>>> Events(
        string runId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var validation = ValidatePagination(limit, offset);
        if (validation != null) return validation;

        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        var allEvents = archive.Events;
        var total = allEvents.Count;
        var paged = allEvents.Skip(offset).Take(limit).ToList();

        var items = paged.Select(ToEventDto).ToList();

        return Ok(new PagedResultDto<SubAgentRunEventDto>
        {
            Items = items,
            Total = total,
            Offset = offset,
            Limit = limit,
        });
    }

    /// <summary>
    /// 将 events.jsonl 中的 event object 转换为 SubAgentRunEventDto。
    /// event object 是已反序列化的 JsonElement 或匿名对象，需要兼容两种形式。
    /// </summary>
    private static SubAgentRunEventDto ToEventDto(object rawEvent)
    {
        // rawEvent 来自 System.Text.Json 反序列化，为 JsonElement
        if (rawEvent is JsonElement je)
        {
            var eventId = je.TryGetProperty("eventId", out var eid) ? eid.GetString() ?? ""
                : je.TryGetProperty("event_id", out var eid2) ? eid2.GetString() ?? ""
                : "";
            var eventType = je.TryGetProperty("eventType", out var et) ? et.GetString() ?? ""
                : je.TryGetProperty("type", out var et2) ? et2.GetString() ?? ""
                : "unknown";
            var timestamp = je.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? ""
                : je.TryGetProperty("recordedAt", out var ra) ? ra.GetString() ?? ""
                : "";

            // 计算 payload 大小和预览
            var rawJson = je.GetRawText();
            var payloadSize = Encoding.UTF8.GetByteCount(rawJson);
            var payloadPreview = rawJson.Length > 200
                ? rawJson[..200] + "..."
                : rawJson;

            return new SubAgentRunEventDto
            {
                EventId = eventId,
                EventType = eventType,
                Timestamp = timestamp,
                PayloadSize = payloadSize,
                PayloadPreview = payloadPreview,
            };
        }

        // fallback：非 JsonElement 的 object
        return new SubAgentRunEventDto
        {
            EventId = rawEvent.GetType().GetProperty("EventId")?.GetValue(rawEvent)?.ToString() ?? "",
            EventType = rawEvent.GetType().GetProperty("EventType")?.GetValue(rawEvent)?.ToString() ?? "unknown",
            Timestamp = rawEvent.GetType().GetProperty("Timestamp")?.GetValue(rawEvent)?.ToString() ?? "",
            PayloadSize = 0,
            PayloadPreview = null,
        };
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/tools — 获取工具审计列表（从 tools.jsonl 读取）。
    /// 返回 PagedResultDto&lt;SubAgentToolAuditEntry&gt;（DTO 已稳定）。
    /// </summary>
    [HttpGet("{runId}/tools")]
    public async Task<ActionResult<PagedResultDto<SubAgentToolAuditEntry>>> Tools(
        string runId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var validation = ValidatePagination(limit, offset);
        if (validation != null) return validation;

        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        var allTools = archive.Tools;
        var total = allTools.Count;
        var paged = allTools.Skip(offset).Take(limit).ToList();

        return Ok(new PagedResultDto<SubAgentToolAuditEntry>
        {
            Items = paged,
            Total = total,
            Offset = offset,
            Limit = limit,
        });
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/output — 获取最终输出内容（output.md 内容）。
    /// </summary>
    [HttpGet("{runId}/output")]
    public async Task<ActionResult<object>> Output(string runId, CancellationToken ct)
    {
        var archive = await _runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null)
            return NotFound(new { error = $"Run '{runId}' not found." });

        return Ok(new { output = archive.Output });
    }
}
