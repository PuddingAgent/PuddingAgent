using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 结构化遥测指标查询 API，用于后台统计分析和日志分析入口。
/// </summary>
[Authorize]
[ApiController]
[Route("api/diagnostics/metrics")]
public sealed class TelemetryMetricsController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public TelemetryMetricsController(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/diagnostics/metrics/summary
    /// 按 category/name/status 聚合最近的遥测事实。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<TelemetryMetricsSummaryDto>> GetSummary(
        [FromQuery] string? workspaceId,
        [FromQuery] string? sessionId,
        [FromQuery] string? category,
        [FromQuery] string? name,
        [FromQuery] string? sinceUtc,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var maxGroups = Math.Clamp(limit, 1, 500);
        var query = _db.TelemetryMetricEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(e => e.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => e.SessionId == sessionId);
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category);
        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(e => e.Name == name);
        var rows = await query.ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(sinceUtc))
            rows = rows
                .Where(e => string.CompareOrdinal(e.OccurredAtUtc, sinceUtc) >= 0)
                .ToList();

        var groups = rows
            .GroupBy(e => new { e.Category, e.Name, e.Status })
            .Select(g => new TelemetryMetricSummaryGroupDto(
                g.Key.Category,
                g.Key.Name,
                g.Key.Status,
                g.Count(),
                g.Sum(e => e.CountValue ?? 0),
                g.Average(e => (double?)e.DurationMs),
                g.Max(e => e.DurationMs),
                g.Count(e => e.Severity == "error"),
                g.Max(e => e.OccurredAtUtc)!))
            .OrderByDescending(g => g.LastOccurredAtUtc)
            .Take(maxGroups)
            .ToList();

        return Ok(new TelemetryMetricsSummaryDto(
            TotalGroups: groups.Count,
            Groups: groups));
    }
}

public sealed record TelemetryMetricsSummaryDto(
    int TotalGroups,
    IReadOnlyList<TelemetryMetricSummaryGroupDto> Groups);

public sealed record TelemetryMetricSummaryGroupDto(
    string Category,
    string Name,
    string? Status,
    int EventCount,
    long CountValueSum,
    double? AverageDurationMs,
    long? MaxDurationMs,
    int ErrorCount,
    string LastOccurredAtUtc);
