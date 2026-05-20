using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Diagnostics;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 运行时诊断 Timeline API — 提供泛用 Timeline 查询、会话 Timeline、组件健康状态和 E2E 证据导出。
/// 关联 ADR：Docs/07架构/23运行时可观测性闭环与E2E验证基线ADR.md
/// </summary>
[ApiController]
[Route("api/diagnostics")]
[Authorize]
public class DiagnosticsTimelineController : ControllerBase
{
    private readonly RuntimeTimelineQueryService _timelineService;
    private readonly IDiagnosticRedactor _redactor;

    public DiagnosticsTimelineController(
        RuntimeTimelineQueryService timelineService,
        IDiagnosticRedactor redactor)
    {
        _timelineService = timelineService;
        _redactor = redactor;
    }

    /// <summary>
    /// GET /api/diagnostics/runtime/timeline
    /// 泛用 Timeline 查询 — 支持 WorkspaceId/SessionId/TraceId/Component/Status 筛选 + 分页。
    /// </summary>
    [HttpGet("runtime/timeline")]
    public async Task<ActionResult<PagedTimelineResultDto>> GetTimeline(
        [FromQuery] RuntimeTimelineQueryDto query,
        CancellationToken ct)
    {
        var result = await _timelineService.QueryTimelineAsync(query, ct);

        // 脱敏处理
        var redactedItems = result.Items.Select(RedactItem).ToList();
        return Ok(new PagedTimelineResultDto
        {
            Items = redactedItems,
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
        });
    }

    /// <summary>
    /// GET /api/diagnostics/sessions/{sessionId}/timeline
    /// 会话 Timeline — 返回指定会话的全部事件（不分页）。
    /// </summary>
    [HttpGet("sessions/{sessionId}/timeline")]
    public async Task<ActionResult<IReadOnlyList<RuntimeTimelineItemDto>>> GetSessionTimeline(
        [FromRoute] string sessionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { message = "sessionId 不能为空" });

        var items = await _timelineService.GetSessionTimelineAsync(sessionId, ct);
        var redacted = items.Select(RedactItem).ToList();
        return Ok(redacted);
    }

    /// <summary>
    /// GET /api/diagnostics/runtime/components
    /// 组件健康状态 — 基于最近 24h RuntimeActivity 数据的聚合快照。
    /// </summary>
    [HttpGet("runtime/components")]
    public async Task<ActionResult<IReadOnlyList<RuntimeComponentHealthDto>>> GetComponentHealth(
        CancellationToken ct)
    {
        var health = await _timelineService.GetComponentHealthAsync(ct);
        return Ok(health);
    }

    /// <summary>
    /// GET /api/diagnostics/e2e/evidence/{traceId}
    /// E2E 证据导出 — 按 TraceId 获取完整诊断证据链。
    /// </summary>
    [HttpGet("e2e/evidence/{traceId}")]
    public async Task<ActionResult<DiagnosticEvidenceDto>> GetE2EEvidence(
        [FromRoute] string traceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(traceId))
            return BadRequest(new { message = "traceId 不能为空" });

        var evidence = await _timelineService.GetE2EEvidenceAsync(traceId, ct);
        if (evidence == null)
            return NotFound(new { message = $"未找到 TraceId={traceId} 的证据" });

        // 脱敏 Timeline
        var redactedTimeline = evidence.Timeline.Select(RedactItem).ToList();
        return Ok(new DiagnosticEvidenceDto
        {
            TraceId = evidence.TraceId,
            SessionId = evidence.SessionId,
            RunId = evidence.RunId,
            Timeline = redactedTimeline,
            SubAgentRuns = evidence.SubAgentRuns,
        });
    }

    /// <summary>脱敏单条 Timeline 项的 Metadata 和 Summary/Error 文本。</summary>
    private RuntimeTimelineItemDto RedactItem(RuntimeTimelineItemDto item) => item with
    {
        Summary = _redactor.RedactText(item.Summary),
        Error = _redactor.RedactText(item.Error),
        Metadata = _redactor.RedactMetadata(item.Metadata),
    };
}
