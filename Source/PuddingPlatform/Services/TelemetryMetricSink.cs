using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Observability;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 将结构化遥测事实写入平台 SQLite。写入失败只记录 warning，不阻断业务链路。
/// </summary>
public sealed class TelemetryMetricSink : ITelemetryMetricSink
{
    private const int MaxDebugJsonLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryMetricSink> _logger;

    public TelemetryMetricSink(
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetryMetricSink> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            db.TelemetryMetricEvents.Add(ToEntity(metric));
            await db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "[TelemetryMetric] trace={TraceId} session={SessionId} category={Category} name={Name} status={Status} durationMs={DurationMs}",
                metric.Trace.TraceId,
                metric.Trace.SessionId,
                metric.Category,
                metric.Name,
                metric.Status,
                metric.DurationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "[TelemetryMetric] Invalid telemetry metric id={MetricId} category={Category} name={Name}; refusing to write ambiguous diagnostic data",
                metric.MetricId,
                metric.Category,
                metric.Name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[TelemetryMetric] Failed to record metric id={MetricId} category={Category} name={Name}",
                metric.MetricId,
                metric.Category,
                metric.Name);
            throw;
        }
    }

    private static TelemetryMetricEventEntity ToEntity(TelemetryMetric metric)
    {
        var stage = RuntimePipelineStages.ResolveForMetric(
            metric.Category,
            metric.Name,
            metric.Status);
        var dimensions = RuntimePipelineStages.Enrich(metric.Dimensions, stage);
        var dimensionsJson = dimensions.Count == 0
            ? null
            : JsonSerializer.Serialize(dimensions, JsonOptions);

        return new TelemetryMetricEventEntity
        {
            MetricId = metric.MetricId,
            TraceId = metric.Trace.TraceId,
            CorrelationId = metric.Trace.CorrelationId,
            SessionId = metric.Trace.SessionId,
            WorkspaceId = metric.Trace.WorkspaceId,
            ExecutionId = metric.Trace.ExecutionId,
            ParentExecutionId = metric.Trace.ParentExecutionId,
            SubAgentId = metric.Trace.SubAgentId,
            EventId = metric.Trace.EventId,
            ConnectorId = metric.Trace.ConnectorId,
            UserId = metric.Trace.UserId,
            Source = Truncate(metric.Source, 64) ?? "backend",
            Category = Truncate(metric.Category, 64) ?? "unknown",
            Name = Truncate(metric.Name, 128) ?? "unknown",
            Status = Truncate(metric.Status, 32),
            OccurredAtUtc = metric.OccurredAtUtc.ToString("O"),
            DurationMs = metric.DurationMs,
            CountValue = metric.CountValue,
            NumericValue = metric.NumericValue,
            Unit = Truncate(metric.Unit, 32),
            Severity = Truncate(metric.Severity, 16) ?? "info",
            Summary = Truncate(metric.Summary, 512),
            DimensionsJson = dimensionsJson,
            DebugJson = Truncate(metric.DebugJson, MaxDebugJsonLength),
            ErrorCode = Truncate(metric.ErrorCode, 128),
            ErrorMessage = Truncate(metric.ErrorMessage, 512),
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}
