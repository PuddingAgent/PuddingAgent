using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Diagnostics;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 运行时 Timeline 聚合查询服务 — 从 RuntimeActivity / EventQueue / SessionEventLog / SubAgentRun
/// 四个数据源统一投影为 RuntimeTimelineItemDto，按 StartedAtUtc 降序排序并分页返回。
/// 
/// 注入 IDbContextFactory&lt;PlatformDbContext&gt; 以支持被 Singleton service 消费。
/// 关联 ADR：Docs/07架构/23运行时可观测性闭环与E2E验证基线ADR.md
/// </summary>
public class RuntimeTimelineQueryService
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RuntimeTimelineQueryService(IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 按查询条件聚合 Timeline 并分页返回。
    /// 聚合顺序：RuntimeActivity → EventQueue → SessionEventLog → SubAgentRun
    /// 统一投影后按 StartedAtUtc 降序排序。
    /// </summary>
    public async Task<PagedTimelineResultDto> QueryTimelineAsync(
        RuntimeTimelineQueryDto query,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = new List<RuntimeTimelineItemDto>();

        // 1. 从 RuntimeActivities 查询
        if (ShouldQueryKind(query, null))
        {
            var activities = await QueryActivitiesAsync(db, query, ct);
            items.AddRange(activities);
        }

        // 2. 从 EventQueue 查询
        if (ShouldQueryKind(query, null))
        {
            var events = await QueryEventsAsync(db, query, ct);
            items.AddRange(events);
        }

        // 3. 从 SessionEventLog 查询
        if (ShouldQueryKind(query, null))
        {
            var sessionFrames = await QuerySessionEventsAsync(db, query, ct);
            items.AddRange(sessionFrames);
        }

        // 4. 从 SubAgentRuns 查询
        if (ShouldQueryKind(query, null))
        {
            var subAgentRuns = await QuerySubAgentRunsAsync(db, query, ct);
            items.AddRange(subAgentRuns);
        }

        // 按 StartedAtUtc 排序 + 分页
        var sorted = (query.SortOrder == "asc"
            ? items.OrderBy(i => i.StartedAtUtc)
            : items.OrderByDescending(i => i.StartedAtUtc))
            .ToList();

        var total = sorted.Count;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var paged = sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedTimelineResultDto
        {
            Items = paged,
            Page = page,
            PageSize = pageSize,
            Total = total,
        };
    }

    /// <summary>
    /// 查询指定会话的完整 Timeline（不分页，用于详情展示）。
    /// </summary>
    public async Task<IReadOnlyList<RuntimeTimelineItemDto>> GetSessionTimelineAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = new List<RuntimeTimelineItemDto>();

        // RuntimeActivities
        var activities = await db.RuntimeActivities
            .Where(a => a.SessionId == sessionId)
            .Select(a => MapActivityToDto(a))
            .ToListAsync(ct);
        items.AddRange(activities);

        // EventQueue
        var events = await db.EventQueue
            .Where(e => e.SessionId == sessionId)
            .Select(e => MapEventToDto(e))
            .ToListAsync(ct);
        items.AddRange(events);

        // SessionEventLog
        var sessionFrames = await db.SessionEventLogs
            .Where(s => s.SessionId == sessionId)
            .Select(s => MapSessionEventToDto(s))
            .ToListAsync(ct);
        items.AddRange(sessionFrames);

        // SubAgentRuns
        var subRuns = await db.SubAgentRuns
            .Where(r => r.ParentSessionId == sessionId)
            .Select(r => MapSubAgentRunToDto(r))
            .ToListAsync(ct);
        items.AddRange(subRuns);

        return items
            .OrderByDescending(i => i.StartedAtUtc)
            .ToList();
    }

    /// <summary>
    /// 按 TraceId 获取完整 E2E 证据链（Timeline + 子代理摘要）。
    /// </summary>
    public async Task<DiagnosticEvidenceDto?> GetE2EEvidenceAsync(
        string traceId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = new List<RuntimeTimelineItemDto>();

        // 按 TraceId 从所有数据源聚合
        var activities = await db.RuntimeActivities
            .Where(a => a.TraceId == traceId)
            .Select(a => MapActivityToDto(a))
            .ToListAsync(ct);
        items.AddRange(activities);

        var events = await db.EventQueue
            .Where(e => e.TraceId == traceId)
            .Select(e => MapEventToDto(e))
            .ToListAsync(ct);
        items.AddRange(events);

        var sessionFrames = await db.SessionEventLogs
            .Where(s => s.TraceId == traceId)
            .Select(s => MapSessionEventToDto(s))
            .ToListAsync(ct);
        items.AddRange(sessionFrames);

        var subRuns = await db.SubAgentRuns
            .Where(r => r.TraceId == traceId)
            .Select(r => MapSubAgentRunToDto(r))
            .ToListAsync(ct);
        items.AddRange(subRuns);

        if (items.Count == 0)
            return null;

        var timeline = items
            .OrderBy(i => i.StartedAtUtc)
            .ToList();

        // SubAgentRun 摘要（仅来自 SubAgentRuns 表）
        var subAgentSummaries = await db.SubAgentRuns
            .Where(r => r.TraceId == traceId)
            .Select(r => MapToSubAgentSummary(r))
            .ToListAsync(ct);

        var sessionId = timeline
            .Select(i => i.SessionId)
            .FirstOrDefault(s => s != null);

        var runId = timeline
            .Select(i => i.RunId)
            .FirstOrDefault(r => r != null);

        return new DiagnosticEvidenceDto
        {
            TraceId = traceId,
            SessionId = sessionId,
            RunId = runId,
            Timeline = timeline,
            SubAgentRuns = subAgentSummaries,
        };
    }

    /// <summary>
    /// 查询各组件健康状态快照 — 基于 RuntimeActivity 最近 24h 数据聚合。
    /// Component 健康状态推断：
    ///   - healthy: 成功率 >= 95%
    ///   - degraded: 成功率 >= 70%
    ///   - failing: 成功率 < 70% 或有近期失败
    ///   - unknown: 无数据
    /// </summary>
    public async Task<IReadOnlyList<RuntimeComponentHealthDto>> GetComponentHealthAsync(
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTimeOffset.UtcNow.AddHours(-24).ToString("O");

        var stats = await db.RuntimeActivities
            .Where(a => string.Compare(a.StartedAtUtc, since) >= 0)
            .GroupBy(a => a.Component)
            .Select(g => new
            {
                Component = g.Key,
                StartedCount = g.Count(),
                SucceededCount = g.Count(a => a.Status == "succeeded" || a.Status == "success"),
                FailedCount = g.Count(a => a.Status == "failed" || a.Status == "error"),
                RetriedCount = g.Count(a => a.Status == "retried" || a.Status == "retry"),
                CancelledCount = g.Count(a => a.Status == "cancelled"),
                LastSeenAtUtc = g.Max(a => a.StartedAtUtc),
                LastError = g.Where(a => a.ErrorMessage != null)
                    .OrderByDescending(a => a.StartedAtUtc)
                    .Select(a => a.ErrorMessage)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return stats.Select(s =>
        {
            var total = s.StartedCount;
            var rate = total > 0 ? (double)s.SucceededCount / total : 1.0;
            var status = total == 0 ? "unknown"
                : rate >= 0.95 ? "healthy"
                : rate >= 0.70 ? "degraded"
                : "failing";

            return new RuntimeComponentHealthDto
            {
                Component = s.Component,
                Status = status,
                StartedCount = s.StartedCount,
                SucceededCount = s.SucceededCount,
                FailedCount = s.FailedCount,
                RetriedCount = s.RetriedCount,
                CancelledCount = s.CancelledCount,
                LastSeenAtUtc = s.LastSeenAtUtc,
                LastError = s.LastError,
            };
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // 查询辅助
    // ═══════════════════════════════════════════════════════════════

    private static bool ShouldQueryKind(RuntimeTimelineQueryDto query, string? kind)
    {
        // 当前不支持按 Kind 筛选，查询所有数据源
        return true;
    }

    private async Task<List<RuntimeTimelineItemDto>> QueryActivitiesAsync(
        PlatformDbContext db, RuntimeTimelineQueryDto query, CancellationToken ct)
    {
        IQueryable<RuntimeActivityEntity> q = db.RuntimeActivities;
        q = ApplyCommonFilters(q, query);
        return await q.Select(a => MapActivityToDto(a)).ToListAsync(ct);
    }

    private async Task<List<RuntimeTimelineItemDto>> QueryEventsAsync(
        PlatformDbContext db, RuntimeTimelineQueryDto query, CancellationToken ct)
    {
        IQueryable<EventQueueEntity> q = db.EventQueue;
        q = ApplyCommonFilters(q, query);
        return await q.Select(e => MapEventToDto(e)).ToListAsync(ct);
    }

    private async Task<List<RuntimeTimelineItemDto>> QuerySessionEventsAsync(
        PlatformDbContext db, RuntimeTimelineQueryDto query, CancellationToken ct)
    {
        IQueryable<SessionEventLogEntity> q = db.SessionEventLogs;
        q = ApplyCommonFilters(q, query);
        return await q.Select(s => MapSessionEventToDto(s)).ToListAsync(ct);
    }

    private async Task<List<RuntimeTimelineItemDto>> QuerySubAgentRunsAsync(
        PlatformDbContext db, RuntimeTimelineQueryDto query, CancellationToken ct)
    {
        IQueryable<SubAgentRunEntity> q = db.SubAgentRuns;
        q = ApplyCommonFilters(q, query);
        return await q.Select(r => MapSubAgentRunToDto(r)).ToListAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // 通用筛选器 — 适用于所有有这些字段的实体
    // ═══════════════════════════════════════════════════════════════

    private static IQueryable<RuntimeActivityEntity> ApplyCommonFilters(
        IQueryable<RuntimeActivityEntity> q, RuntimeTimelineQueryDto query)
    {
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            q = q.Where(a => a.WorkspaceId == query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.SessionId))
            q = q.Where(a => a.SessionId == query.SessionId);
        if (!string.IsNullOrWhiteSpace(query.TraceId))
            q = q.Where(a => a.TraceId == query.TraceId);
        if (!string.IsNullOrWhiteSpace(query.Component))
            q = q.Where(a => a.Component == query.Component);
        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(a => a.Status == query.Status);
        return q;
    }

    private static IQueryable<EventQueueEntity> ApplyCommonFilters(
        IQueryable<EventQueueEntity> q, RuntimeTimelineQueryDto query)
    {
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            q = q.Where(e => e.WorkspaceId == query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.SessionId))
            q = q.Where(e => e.SessionId == query.SessionId);
        if (!string.IsNullOrWhiteSpace(query.TraceId))
            q = q.Where(e => e.TraceId == query.TraceId);
        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(e => e.Status == query.Status);
        return q;
    }

    private static IQueryable<SessionEventLogEntity> ApplyCommonFilters(
        IQueryable<SessionEventLogEntity> q, RuntimeTimelineQueryDto query)
    {
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            q = q.Where(s => s.WorkspaceId == query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.SessionId))
            q = q.Where(s => s.SessionId == query.SessionId);
        if (!string.IsNullOrWhiteSpace(query.TraceId))
            q = q.Where(s => s.TraceId == query.TraceId);
        return q;
    }

    private static IQueryable<SubAgentRunEntity> ApplyCommonFilters(
        IQueryable<SubAgentRunEntity> q, RuntimeTimelineQueryDto query)
    {
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            q = q.Where(r => r.WorkspaceId == query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.SessionId))
            q = q.Where(r => r.ParentSessionId == query.SessionId
                || r.SubSessionId == query.SessionId);
        if (!string.IsNullOrWhiteSpace(query.TraceId))
            q = q.Where(r => r.TraceId == query.TraceId);
        if (!string.IsNullOrWhiteSpace(query.AgentInstanceId))
            q = q.Where(r => r.AgentInstanceId == query.AgentInstanceId);
        if (!string.IsNullOrWhiteSpace(query.RunId))
            q = q.Where(r => r.RunId == query.RunId);
        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(r => r.Status == query.Status);
        return q;
    }

    // ═══════════════════════════════════════════════════════════════
    // Entity → DTO 投影
    // ═══════════════════════════════════════════════════════════════

    private static RuntimeTimelineItemDto MapActivityToDto(RuntimeActivityEntity a) => new()
    {
        Id = a.ActivityId,
        Kind = "activity",
        Component = a.Component,
        Operation = a.Operation,
        Status = a.Status,
        WorkspaceId = a.WorkspaceId,
        SessionId = a.SessionId,
        AgentInstanceId = a.SubAgentId,
        RunId = a.ExecutionId,
        EventId = a.EventId,
        TraceId = a.TraceId,
        CorrelationId = a.CorrelationId,
        StartedAtUtc = ParseDateTimeOffset(a.StartedAtUtc),
        CompletedAtUtc = TryParseDateTimeOffset(a.EndedAtUtc),
        DurationMs = a.DurationMs,
        Summary = a.Summary,
        Error = a.ErrorMessage,
        Metadata = ParseMetadataJson(a.MetadataJson),
    };

    private static RuntimeTimelineItemDto MapEventToDto(EventQueueEntity e) => new()
    {
        Id = e.EventId,
        Kind = "event",
        Component = "event_queue",
        Operation = e.EventType,
        Status = e.Status,
        WorkspaceId = e.WorkspaceId,
        SessionId = e.SessionId,
        AgentInstanceId = e.AgentId ?? e.SubAgentId,
        EventId = e.EventId,
        TraceId = e.TraceId,
        CorrelationId = e.CorrelationId,
        StartedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(e.CreatedAt > 0 ? e.CreatedAt : 0),
        CompletedAtUtc = e.CompletedAt.HasValue && e.CompletedAt > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(e.CompletedAt.Value)
            : null,
        DurationMs = e.StartedAt.HasValue && e.CompletedAt.HasValue
            ? e.CompletedAt.Value - e.StartedAt.Value
            : null,
        Summary = e.EventType,
        Error = e.ErrorMessage,
        Metadata = new Dictionary<string, string>(),
    };

    private static RuntimeTimelineItemDto MapSessionEventToDto(SessionEventLogEntity s) => new()
    {
        Id = s.Id.ToString(),
        Kind = "session_frame",
        Component = s.Component ?? "session_state",
        Operation = s.Operation ?? s.EventType,
        Status = "recorded",
        WorkspaceId = s.WorkspaceId,
        SessionId = s.SessionId,
        TraceId = s.TraceId,
        CorrelationId = s.CorrelationId,
        StartedAtUtc = ParseDateTimeOffset(s.RecordedAt),
        CompletedAtUtc = null,
        DurationMs = null,
        Summary = s.EventType,
        Error = null,
        Metadata = new Dictionary<string, string>(),
    };

    private static RuntimeTimelineItemDto MapSubAgentRunToDto(SubAgentRunEntity r) => new()
    {
        Id = r.RunId,
        Kind = "subagent_run",
        Component = "subagent",
        Operation = $"subagent_run:{r.TemplateId}",
        Status = r.Status,
        WorkspaceId = r.WorkspaceId,
        SessionId = r.ParentSessionId,
        AgentInstanceId = r.AgentInstanceId,
        RunId = r.RunId,
        TraceId = r.TraceId,
        CorrelationId = r.CorrelationId,
        StartedAtUtc = ParseDateTimeOffset(r.StartedAt),
        CompletedAtUtc = TryParseDateTimeOffset(r.CompletedAt),
        DurationMs = r.TotalDurationMs > 0 ? r.TotalDurationMs : null,
        Summary = $"TotalRounds={r.TotalRounds}, TotalToolCalls={r.TotalToolCalls}",
        Error = r.ErrorMessage,
        Metadata = new Dictionary<string, string>
        {
            ["sub_session_id"] = r.SubSessionId,
            ["template_id"] = r.TemplateId,
            ["total_rounds"] = r.TotalRounds.ToString(),
            ["total_tool_calls"] = r.TotalToolCalls.ToString(),
        },
    };

    private static SubAgentRunSummaryDto MapToSubAgentSummary(SubAgentRunEntity r) => new()
    {
        RunId = r.RunId,
        ParentSessionId = r.ParentSessionId,
        SubSessionId = r.SubSessionId,
        WorkspaceId = r.WorkspaceId,
        AgentInstanceId = r.AgentInstanceId,
        TemplateId = r.TemplateId,
        Status = r.Status,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        TotalDurationMs = r.TotalDurationMs,
        TotalRounds = r.TotalRounds,
        TotalToolCalls = r.TotalToolCalls,
        ErrorMessage = r.ErrorMessage,
    };

    // ═══════════════════════════════════════════════════════════════
    // 解析辅助
    // ═══════════════════════════════════════════════════════════════

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        if (DateTimeOffset.TryParse(value, out var result))
            return result;
        // fallback: 尝试 Unix 毫秒
        if (long.TryParse(value, out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(value, out var result))
            return result;
        if (long.TryParse(value, out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }

    private static IReadOnlyDictionary<string, string> ParseMetadataJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
            if (dict == null)
                return new Dictionary<string, string>();
            return dict.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ValueKind == JsonValueKind.String
                    ? kvp.Value.GetString() ?? kvp.Value.ToString()
                    : kvp.Value.ToString());
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
