using PuddingCode.Observability;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services;

/// <summary>
/// 执行生命周期记录器，将 ExecutionLifecycleRecord 映射到 RuntimeActivity 并持久化。
/// </summary>
public sealed class RuntimeActivityExecutionLifecycleRecorder : IExecutionLifecycleRecorder
{
    private readonly IRuntimeActivitySink _sink;

    public RuntimeActivityExecutionLifecycleRecorder(IRuntimeActivitySink sink)
    {
        _sink = sink;
    }

    public async Task<string> StartAsync(ExecutionLifecycleRecord record, CancellationToken ct = default)
    {
        var trace = RuntimeTraceContext.CreateNew(
            sessionId: record.SessionId,
            workspaceId: record.WorkspaceId,
            executionId: record.ExecutionId,
            correlationId: record.CorrelationId ?? record.TraceId);

        var activity = new RuntimeActivity
        {
            Trace = trace,
            Component = record.Component,
            Operation = record.Operation,
            Status = RuntimeActivityStatuses.Started,
            StartedAtUtc = record.StartedAtUtc,
            Summary = record.Summary,
            Metadata = record.Metadata.Count > 0 ? record.Metadata : null,
        };

        await _sink.RecordAsync(activity, ct);
        return activity.ActivityId;
    }

    public async Task CompleteAsync(string activityId, string status, string? summary = null, string? error = null, CancellationToken ct = default)
    {
        // Complete 创建一条新的 RuntimeActivity 记录，通过 Metadata 关联到原始 activity
        var trace = RuntimeTraceContext.CreateNew(correlationId: activityId);

        var activity = new RuntimeActivity
        {
            Trace = trace,
            Component = "agent_execution",
            Operation = "lifecycle_complete",
            Status = status,
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Summary = summary,
            ErrorMessage = error,
            Metadata = new Dictionary<string, string>
            {
                ["lifecycle_activity_id"] = activityId
            }
        };

        await _sink.RecordAsync(activity, ct);
    }

    public async Task RecordInstantAsync(ExecutionLifecycleRecord record, CancellationToken ct = default)
    {
        var trace = RuntimeTraceContext.CreateNew(
            sessionId: record.SessionId,
            workspaceId: record.WorkspaceId,
            executionId: record.ExecutionId,
            correlationId: record.CorrelationId ?? record.TraceId);

        var activity = new RuntimeActivity
        {
            Trace = trace,
            Component = record.Component,
            Operation = record.Operation,
            Status = record.Status,
            StartedAtUtc = record.StartedAtUtc,
            EndedAtUtc = record.CompletedAtUtc,
            DurationMs = record.DurationMs,
            Summary = record.Summary,
            ErrorMessage = record.Error,
            Metadata = record.Metadata.Count > 0 ? record.Metadata : null,
        };

        await _sink.RecordAsync(activity, ct);
    }
}
