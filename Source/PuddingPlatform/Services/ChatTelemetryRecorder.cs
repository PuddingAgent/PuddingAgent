using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatform.Services;

/// <summary>
/// Chat API 遥测记录器 — Timeline 轨迹 + Telemetry 指标的二合一封装。
/// </summary>
/// <remarks>
/// <para><b>记录两种observability信号：</b></para>
/// <list type="number">
/// <item>
///   <description>
///     <b>Timeline（会话轨迹）</b>：按时间线记录关键阶段（received → route.resolved → main_session.resolved → command.accepted）。
///     存储在 session_event_log 或 session_timeline 中，供前端 /api/sessions/{id}/trace 查询。
///   </description>
/// </item>
/// <item>
///   <description>
///     <b>Telemetry（业务指标）</b>：记录计数型指标（session.message.received、session.steering.created 等）。
///     写入 ITelemetryMetricSink（内存队列 → TokenUsageRecorder / 控制台输出）。
///   </description>
/// </item>
/// </list>
///
/// <para><b>都通过 SafeRecorder 包裹：异常只记 Warning 不抛出，不阻断主流程（ChatApiController.SendMessage）。</b></para>
///
/// <para><b>使用位置：</b>ChatApiController.SendMessage、ChatSystemCommandService、SessionEventsController。</para>
/// </remarks>
public sealed class ChatTelemetryRecorder
{
    private readonly ISessionTimelineRecorder _timeline;
    private readonly ITelemetryMetricSink _telemetry;
    private readonly ILogger<ChatTelemetryRecorder> _logger;

    public ChatTelemetryRecorder(
        ISessionTimelineRecorder timeline,
        ITelemetryMetricSink telemetry,
        ILogger<ChatTelemetryRecorder> logger)
    {
        _timeline = timeline;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// 记录一条 Timeline 事件（session 生命周期中的关键阶段）。
    /// </summary>
    /// <param name="trace">traceId + sessionId + workspaceId 上下文。</param>
    /// <param name="component">组件名（如 RuntimeActivityComponents.AgentExecution）。</param>
    /// <param name="stage">阶段标识（如 "chat.post.received"、"chat.route.resolved"）。</param>
    /// <param name="operation">操作类别（如 "chat.send"、"chat.route"）。</param>
    /// <param name="status">状态：Started | Succeeded | Failed | Recorded。</param>
    /// <param name="durationMs">耗时（可选）。</param>
    /// <param name="metadata">附加维度（agentId、audience、targetCount 等）。</param>
    /// <param name="errorMessage">失败时的错误消息。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task RecordTimelineAsync(
        RuntimeTraceContext trace,
        string component,
        string stage,
        string operation,
        string status,
        long? durationMs = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        // SafeRecorder 确保 timeline 写入异常不抛出，不阻断 ChatApiController HTTP 响应
        await SafeRecorder.RunAsync(
            ct2 => _timeline.RecordAsync(new SessionTimelineRecord
            {
                Trace = trace,
                Component = component,
                Stage = stage,
                Operation = operation,
                Status = status,
                DurationMs = durationMs,
                Metadata = metadata,
                ErrorMessage = errorMessage,
            }, ct2),
            _logger,
            $"timeline:{stage}",
            ct);
    }

    /// <summary>
    /// 记录一条 Telemetry 指标（计数/耗时型业务度量）。
    /// </summary>
    /// <param name="trace">traceId + sessionId + workspaceId 上下文。</param>
    /// <param name="category">指标类别（如 TelemetryMetricCategories.Session）。</param>
    /// <param name="name">指标名（如 "session.message.received"）。</param>
    /// <param name="status">Started | Succeeded | Failed | Recorded。</param>
    /// <param name="durationMs">耗时。</param>
    /// <param name="countValue">计数值（1 表示一次事件）。</param>
    /// <param name="dimensions">维度标签（agent_id、audience、message_chars 等）。</param>
    /// <param name="occurredAtUtc">发生时间，默认 DateTimeOffset.UtcNow。</param>
    /// <param name="error">异常对象（自动提取 ErrorCode 和 ErrorMessage）。</param>
    /// <param name="errorMessage">额外的错误描述。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task RecordTelemetryMetricAsync(
        RuntimeTraceContext trace,
        string category,
        string name,
        string status,
        long? durationMs,
        long? countValue,
        IReadOnlyDictionary<string, string>? dimensions = null,
        DateTimeOffset? occurredAtUtc = null,
        Exception? error = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        // SafeRecorder 确保 telemetry 写入异常不抛出，不阻断 ChatApiController HTTP 响应
        await SafeRecorder.RunAsync(
            ct2 => _telemetry.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "backend",
                Category = category,
                Name = name,
                Status = status,
                OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
                DurationMs = durationMs,
                CountValue = countValue,
                Unit = countValue is null ? null : "event",
                Severity = error is null && status != TelemetryMetricStatuses.Failed ? "info" : "error",
                Summary = name,
                Dimensions = dimensions,
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message ?? errorMessage,
            }, ct2),
            _logger,
            $"telemetry:{name}",
            ct);
    }
}
