using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Observability;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed class RuntimeActivitySink : IRuntimeActivitySink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeActivitySink> _logger;

    public RuntimeActivitySink(
        IServiceScopeFactory scopeFactory,
        ILogger<RuntimeActivitySink> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            db.RuntimeActivities.Add(ToEntity(activity));
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[RuntimeActivity] trace={TraceId} session={SessionId} exec={ExecutionId} component={Component} op={Operation} status={Status} durationMs={DurationMs}",
                activity.Trace.TraceId,
                activity.Trace.SessionId,
                activity.Trace.ExecutionId,
                activity.Component,
                activity.Operation,
                activity.Status,
                activity.DurationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[RuntimeActivity] Failed to record activity id={ActivityId} component={Component} op={Operation}",
                activity.ActivityId,
                activity.Component,
                activity.Operation);
        }
    }

    public async Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
        RuntimeActivityQuery query,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var activities = db.RuntimeActivities.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.TraceId))
            activities = activities.Where(a => a.TraceId == query.TraceId);
        if (!string.IsNullOrWhiteSpace(query.SessionId))
            activities = activities.Where(a => a.SessionId == query.SessionId);
        if (!string.IsNullOrWhiteSpace(query.ExecutionId))
            activities = activities.Where(a => a.ExecutionId == query.ExecutionId);
        if (!string.IsNullOrWhiteSpace(query.Component))
            activities = activities.Where(a => a.Component == query.Component);

        var rows = await activities
            .OrderByDescending(a => a.StartedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    private static RuntimeActivityEntity ToEntity(RuntimeActivity activity)
    {
        var metadataJson = activity.Metadata is null
            ? null
            : JsonSerializer.Serialize(activity.Metadata, JsonOptions);

        return new RuntimeActivityEntity
        {
            ActivityId = activity.ActivityId,
            TraceId = activity.Trace.TraceId,
            CorrelationId = activity.Trace.CorrelationId,
            SessionId = activity.Trace.SessionId,
            WorkspaceId = activity.Trace.WorkspaceId,
            ExecutionId = activity.Trace.ExecutionId,
            ParentExecutionId = activity.Trace.ParentExecutionId,
            SubAgentId = activity.Trace.SubAgentId,
            EventId = activity.Trace.EventId,
            ConnectorId = activity.Trace.ConnectorId,
            UserId = activity.Trace.UserId,
            Component = activity.Component,
            Operation = activity.Operation,
            Status = activity.Status,
            StartedAtUtc = activity.StartedAtUtc.ToString("O"),
            EndedAtUtc = activity.EndedAtUtc?.ToString("O"),
            DurationMs = activity.DurationMs,
            Severity = activity.Severity,
            Summary = Truncate(activity.Summary, 512),
            MetadataJson = metadataJson,
            ErrorCode = Truncate(activity.ErrorCode, 128),
            ErrorMessage = Truncate(activity.ErrorMessage, 512),
        };
    }

    private static RuntimeActivity ToDto(RuntimeActivityEntity entity)
    {
        IReadOnlyDictionary<string, string>? metadata = null;
        if (!string.IsNullOrWhiteSpace(entity.MetadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    entity.MetadataJson,
                    JsonOptions);
            }
            catch
            {
                metadata = new Dictionary<string, string>
                {
                    ["metadata_parse_error"] = "true",
                };
            }
        }

        return new RuntimeActivity
        {
            ActivityId = entity.ActivityId,
            Trace = new RuntimeTraceContext
            {
                TraceId = entity.TraceId,
                CorrelationId = entity.CorrelationId,
                SessionId = entity.SessionId,
                WorkspaceId = entity.WorkspaceId,
                ExecutionId = entity.ExecutionId,
                ParentExecutionId = entity.ParentExecutionId,
                SubAgentId = entity.SubAgentId,
                EventId = entity.EventId,
                ConnectorId = entity.ConnectorId,
                UserId = entity.UserId,
            },
            Component = entity.Component,
            Operation = entity.Operation,
            Status = entity.Status,
            StartedAtUtc = DateTimeOffset.Parse(entity.StartedAtUtc),
            EndedAtUtc = string.IsNullOrWhiteSpace(entity.EndedAtUtc)
                ? null
                : DateTimeOffset.Parse(entity.EndedAtUtc),
            DurationMs = entity.DurationMs,
            Severity = entity.Severity,
            Summary = entity.Summary,
            Metadata = metadata,
            ErrorCode = entity.ErrorCode,
            ErrorMessage = entity.ErrorMessage,
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}
