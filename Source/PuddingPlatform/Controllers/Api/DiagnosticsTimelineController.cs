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
    private readonly RuntimeDiagnosticsQueryService _diagnostics;
    private readonly SessionBenchmarkDiagnosticsService _benchmarkService;
    private readonly IDiagnosticRedactor _redactor;

    public DiagnosticsTimelineController(
        RuntimeTimelineQueryService timelineService,
        RuntimeDiagnosticsQueryService diagnostics,
        SessionBenchmarkDiagnosticsService benchmarkService,
        IDiagnosticRedactor redactor)
    {
        _timelineService = timelineService;
        _diagnostics = diagnostics;
        _benchmarkService = benchmarkService;
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
        // 查询与脱敏在同一个应用操作里完成（原生客户端得到同一份已脱敏结果）。
        return Ok(await _diagnostics.QueryTimelineAsync(query, ct));
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

        return Ok(await _diagnostics.GetSessionTimelineAsync(sessionId, ct));
    }

    /// <summary>
    /// GET /api/diagnostics/sessions/{sessionId}/benchmark
    /// Hermes/Pudding 会话基准诊断报告 — 聚合工具、审批、日志、Timeline 与摩擦点评分。
    /// </summary>
    [HttpGet("sessions/{sessionId}/benchmark")]
    public async Task<ActionResult<SessionBenchmarkReportDto>> GetSessionBenchmark(
        [FromRoute] string sessionId,
        [FromQuery] int maxFindings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { message = "sessionId 不能为空" });

        var report = await _benchmarkService.BuildAsync(
            sessionId,
            maxFindings > 0 ? maxFindings : 20,
            ct);
        if (!report.HasEvidence)
            return NotFound(new { message = $"未找到 SessionId={sessionId} 的诊断记录" });

        return Ok(RedactBenchmarkReport(report));
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
        var redactedTimeline = evidence.Timeline.Select(_diagnostics.Redact).ToList();
        return Ok(new DiagnosticEvidenceDto
        {
            TraceId = evidence.TraceId,
            SessionId = evidence.SessionId,
            RunId = evidence.RunId,
            Timeline = redactedTimeline,
            SubAgentRuns = evidence.SubAgentRuns,
        });
    }

    private SessionBenchmarkReportDto RedactBenchmarkReport(SessionBenchmarkReportDto report) => report with
    {
        Failures = report.Failures.Select(result => result with
        {
            Error = _redactor.RedactText(result.Error),
            Output = _redactor.RedactText(result.Output),
            PairedCommand = _redactor.RedactText(result.PairedCommand),
        }).ToList(),
        ApprovalTimeline = report.ApprovalTimeline.Select(evt => evt with
        {
            Command = _redactor.RedactText(evt.Command),
            Reason = _redactor.RedactText(evt.Reason),
        }).ToList(),
        Tickets = report.Tickets.Select(ticket => ticket with
        {
            Command = _redactor.RedactText(ticket.Command),
        }).ToList(),
        TimelineErrors = report.TimelineErrors.Select(error => error with
        {
            ErrorMessage = _redactor.RedactText(error.ErrorMessage),
        }).ToList(),
        SessionLogFindings = report.SessionLogFindings
            .Select(line => _redactor.RedactText(line) ?? "")
            .ToList(),
        FrictionPoints = report.FrictionPoints.Select(point => point with
        {
            Evidence = _redactor.RedactText(point.Evidence) ?? "",
            Impact = _redactor.RedactText(point.Impact) ?? "",
            Recommendation = _redactor.RedactText(point.Recommendation) ?? "",
        }).ToList(),
    };
}
