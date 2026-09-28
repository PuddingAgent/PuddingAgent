using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Diagnostics;
using PuddingCode.SubAgents;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 子代理运行查询应用操作——把运行列表从直接使用 DbContext 的 SubAgentRunController 原位下沉，
/// 并把详情/事件/工具的归档投影集中到一处（这些原先散在控制器私有方法里）。
///
/// 保留 Core 的既有语义：limit 1–500、offset ≥ 0；过滤条件为 parentSessionId/workspaceId/
/// agentInstanceId/status；列表按 StartedAt 倒序；详情里的用时/轮次/工具数在 Manifest 缺失时用归档回填，
/// 并带 archive-degraded 标记（非空表示归档曾丢弃事件）。
/// </summary>
public sealed class SubAgentRunQueryService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ISubAgentRunStore runStore)
{
    public const int MinLimit = 1;
    public const int MaxLimit = 500;

    public static IReadOnlyList<string> Statuses { get; } = ["running", "succeeded", "failed", "cancelled"];

    public static string? ValidatePagination(int limit, int offset)
    {
        if (limit < MinLimit || limit > MaxLimit) return $"limit 必须在 {MinLimit}-{MaxLimit} 之间";
        if (offset < 0) return "offset 必须 >= 0";
        return null;
    }

    public async Task<PagedResultDto<SubAgentRunSummaryDto>> ListAsync(
        string? parentSessionId, string? workspaceId, string? agentInstanceId, string? status,
        int limit, int offset, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.SubAgentRuns.AsQueryable();
        if (!string.IsNullOrWhiteSpace(parentSessionId)) query = query.Where(run => run.ParentSessionId == parentSessionId);
        if (!string.IsNullOrWhiteSpace(workspaceId)) query = query.Where(run => run.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(agentInstanceId)) query = query.Where(run => run.AgentInstanceId == agentInstanceId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(run => run.Status == status);

        var total = await query.CountAsync(ct);
        var entities = await query
            .OrderByDescending(run => run.StartedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return new PagedResultDto<SubAgentRunSummaryDto>
        {
            Items = entities.Select(ToSummaryDto).ToList(),
            Total = total,
            Offset = offset,
            Limit = limit,
        };
    }

    public async Task<SubAgentRunDetailDto?> GetAsync(string runId, CancellationToken ct = default)
    {
        var archive = await runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null) return null;

        var manifest = archive.Manifest;
        // Manifest 缺失时用归档回填用时/轮次/工具数——这是控制器原有的兜底，原样保留。
        var elapsedMs = manifest.CompletedAt is { } completedAt
            ? Math.Max(0L, (long)(completedAt - manifest.StartedAt).TotalMilliseconds)
            : Math.Max(0L, (long)(DateTimeOffset.UtcNow - manifest.StartedAt).TotalMilliseconds);
        var archivedRounds = archive.Events.Count(raw => EventType(raw) == "subagent.round.completed");

        var summary = new SubAgentRunSummaryDto
        {
            RunId = manifest.RunId,
            ParentSessionId = manifest.ParentSessionId,
            SubSessionId = manifest.SubSessionId,
            WorkspaceId = manifest.WorkspaceId,
            AgentInstanceId = manifest.AgentInstanceId,
            TemplateId = manifest.TemplateId,
            Status = manifest.Status,
            StartedAt = manifest.StartedAt.ToString("o"),
            CompletedAt = manifest.CompletedAt?.ToString("o"),
            TotalDurationMs = manifest.TotalDurationMs ?? elapsedMs,
            TotalRounds = manifest.TotalRounds ?? archivedRounds,
            TotalToolCalls = manifest.TotalToolCalls ?? archive.Tools.Count,
            ErrorMessage = manifest.ErrorMessage,
        };

        return new SubAgentRunDetailDto
        {
            Summary = summary,
            Task = manifest.Task,
            Output = archive.Output,
            LlmProfiles = manifest.LlmProfiles,
            Trace = manifest.Trace,
            EventCount = archive.Events.Count,
            ToolCallCount = archive.Tools.Count,
            ArchiveDegraded = archive.Degraded,
        };
    }

    public async Task<PagedResultDto<SubAgentRunEventDto>?> EventsAsync(
        string runId, int limit, int offset, CancellationToken ct = default)
    {
        var archive = await runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null) return null;
        return new PagedResultDto<SubAgentRunEventDto>
        {
            Items = archive.Events.Skip(offset).Take(limit).Select(ToEventDto).ToList(),
            Total = archive.Events.Count,
            Offset = offset,
            Limit = limit,
        };
    }

    public async Task<PagedResultDto<SubAgentToolAuditEntry>?> ToolsAsync(
        string runId, int limit, int offset, CancellationToken ct = default)
    {
        var archive = await runStore.GetRunArchiveAsync(runId, ct);
        if (archive is null) return null;
        return new PagedResultDto<SubAgentToolAuditEntry>
        {
            Items = archive.Tools.Skip(offset).Take(limit).ToList(),
            Total = archive.Tools.Count,
            Offset = offset,
            Limit = limit,
        };
    }

    public async Task<string?> OutputAsync(string runId, CancellationToken ct = default)
        => (await runStore.GetRunArchiveAsync(runId, ct))?.Output;

    public static SubAgentRunSummaryDto ToSummaryDto(SubAgentRunEntity run) => new()
    {
        RunId = run.RunId,
        ParentSessionId = run.ParentSessionId,
        SubSessionId = run.SubSessionId,
        WorkspaceId = run.WorkspaceId,
        AgentInstanceId = run.AgentInstanceId,
        TemplateId = run.TemplateId,
        Status = run.Status,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        TotalDurationMs = run.TotalDurationMs,
        TotalRounds = run.TotalRounds,
        TotalToolCalls = run.TotalToolCalls,
        ErrorMessage = run.ErrorMessage,
    };

    /// <summary>events.jsonl 里的对象可能是 JsonElement，也可能是匿名对象——两种都要能投影。</summary>
    public static SubAgentRunEventDto ToEventDto(object rawEvent)
    {
        if (rawEvent is JsonElement element)
        {
            var eventId = ReadString(element, "eventId") ?? ReadString(element, "event_id") ?? "";
            var eventType = ReadString(element, "eventType") ?? ReadString(element, "type") ?? "unknown";
            var timestamp = ReadString(element, "timestamp") ?? ReadString(element, "recordedAt") ?? "";
            var rawJson = element.GetRawText();
            return new SubAgentRunEventDto
            {
                EventId = eventId,
                EventType = eventType,
                Timestamp = timestamp,
                PayloadSize = Encoding.UTF8.GetByteCount(rawJson),
                PayloadPreview = rawJson.Length > 200 ? rawJson[..200] + "..." : rawJson,
                Payload = element.TryGetProperty("payload", out var payload) ? payload.Clone() : null,
            };
        }

        return new SubAgentRunEventDto
        {
            EventId = rawEvent.GetType().GetProperty("EventId")?.GetValue(rawEvent)?.ToString() ?? "",
            EventType = rawEvent.GetType().GetProperty("EventType")?.GetValue(rawEvent)?.ToString() ?? "unknown",
            Timestamp = rawEvent.GetType().GetProperty("Timestamp")?.GetValue(rawEvent)?.ToString() ?? "",
            PayloadSize = 0,
            PayloadPreview = null,
            Payload = null,
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string? EventType(object rawEvent)
    {
        if (rawEvent is JsonElement element)
            return ReadString(element, "eventType") ?? ReadString(element, "type");
        return rawEvent.GetType().GetProperty("EventType")?.GetValue(rawEvent)?.ToString();
    }
}
