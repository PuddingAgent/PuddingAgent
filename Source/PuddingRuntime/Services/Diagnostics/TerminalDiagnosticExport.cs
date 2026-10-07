using System.Reflection;
using PuddingCode.Diagnostics;
using PuddingCode.Observability;

namespace PuddingRuntime.Services.Diagnostics;

/// <summary>
/// 终态失败的**投影与导出**（可诊断基础设施设计 §12 / §13）。
/// <para>
/// 输入：异常 + 现场（诊断作用域/身份/轮次）；输出：
/// ① 面向界面的简略呈现（标题 / 大概原因 / 建议动作）；
/// ② **可复制的完整现场**（`RenderText()` 人读、`RenderJson()` 机器读，含 errorId/traceId/时间/因果码/阶段/证据/日志定位）。
/// </para>
/// <para>
/// 单独成类的原因：它必须是**可单测的纯投影**——`AgentExecutionService` 里的私有嵌套 DTO 无法直接构造，
/// 以前因此只能靠"接上以后人工看"。投影链（事实 → 事故视图 → 报告载荷）全部走叶子组件的纯函数。
/// </para>
/// </summary>
internal static class TerminalDiagnosticExport
{
    /// <summary>投影所需的身份与轮次上下文（全部为已有事实，不新增采集）。</summary>
    internal sealed record Input
    {
        public required DateTimeOffset TimestampUtc { get; init; }
        public string? SessionId { get; init; }
        public string? TurnId { get; init; }
        public string? MessageId { get; init; }
        public string? TraceId { get; init; }
        public string? ErrorId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? AgentTemplateId { get; init; }
        public string? AgentInstanceId { get; init; }
        public string? ExecutionId { get; init; }
        public string? ProviderId { get; init; }
        public string? ModelId { get; init; }
        public string? EndpointHost { get; init; }
        public string TerminalStatus { get; init; } = "failed";
        public int Round { get; init; }
        public int MaxRounds { get; init; }
        public int ConsecutiveFailures { get; init; }
    }

    /// <summary>投影结果：界面字段 + 复制载荷（文本/JSON）。</summary>
    internal sealed record Result
    {
        public required DiagnosticCause Cause { get; init; }
        public required ErrorPresentation Presentation { get; init; }
        public required IncidentView Incident { get; init; }
        public required string ReportVersion { get; init; }
        public required string ReportText { get; init; }
        public required string ReportJson { get; init; }

        public string CauseCode => Cause.Code;
        public string Phase => DiagnosticPhases.ToWire(Cause.Phase);
        public string EvidenceJson => SerializeEvidence(Cause.Evidence);
    }

    /// <summary>该载荷的 schema 版本（前端按它决定怎么解析/复制，不靠猜）。</summary>
    public const string ReportVersionValue = DiagnosticReportDocument.SchemaVersion;

    private static readonly string PlatformVersion =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    public static Result Build(Exception error, LlmCallDiagnosticsScope? scope, Input input)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(input);

        // 因果结论优先用 DirectLlmClient 挂载的那一份（它带着当时的作用域事实）；
        // 取不到才用当前现场重新分类（可能缺 request_bytes/phase）。
        var cause = LlmFailureDiagnostics.TryGet(error, out var attached)
            ? attached
            : LlmFailureDiagnostics.Classify(error, scope);

        var presentation = ErrorPresentationCatalog.Present(cause);
        var incident = ProjectIncident(cause, input);
        var report = DiagnosticReportDocument.FromIncident(
            incident,
            capturedAt: input.TimestampUtc,
            evidence: cause.Evidence,
            providerId: input.ProviderId,
            modelId: input.ModelId,
            endpointHost: input.EndpointHost,
            sessionId: input.SessionId,
            workspaceId: input.WorkspaceId,
            agentTemplateId: input.AgentTemplateId,
            agentInstanceId: input.AgentInstanceId,
            executionId: input.ExecutionId,
            platformVersion: PlatformVersion);

        return new Result
        {
            Cause = cause,
            Presentation = presentation,
            Incident = incident,
            ReportVersion = ReportVersionValue,
            ReportText = report.RenderText(),
            ReportJson = report.RenderJson(),
        };
    }

    private static IncidentView ProjectIncident(DiagnosticCause cause, Input input)
    {
        var attempt = int.TryParse(
            cause.Evidence.GetValueOrDefault(DiagnosticEvidenceKeys.Attempt), out var parsedAttempt)
            ? parsedAttempt
            : (int?)null;
        var requestBytes = long.TryParse(
            cause.Evidence.GetValueOrDefault(DiagnosticEvidenceKeys.RequestBytes), out var parsedBytes)
            ? parsedBytes
            : (long?)null;

        var facts = new List<DiagnosticFact>
        {
            new()
            {
                Kind = DiagnosticFactKinds.Attempt,
                Status = cause.Retryable ? "retried" : "failed",
                OccurredAtUtc = input.TimestampUtc,
                TurnId = input.TurnId,
                TraceId = input.TraceId,
                ErrorId = input.ErrorId,
                Attempt = attempt,
                MaxRetries = int.TryParse(
                    cause.Evidence.GetValueOrDefault(DiagnosticEvidenceKeys.MaxRetries), out var maxRetries)
                    ? maxRetries
                    : null,
                CauseCode = cause.Code,
                Phase = DiagnosticPhases.ToWire(cause.Phase),
                RequestBytes = requestBytes,
                UserMessage = cause.UserMessage,
                RemediationHint = cause.RemediationHint,
                Evidence = cause.Evidence,
            },
            new()
            {
                Kind = DiagnosticFactKinds.Terminal,
                Status = input.TerminalStatus,
                OccurredAtUtc = input.TimestampUtc,
                TurnId = input.TurnId,
                TraceId = input.TraceId,
                ErrorId = input.ErrorId,
                CauseCode = cause.Code,
                Phase = DiagnosticPhases.ToWire(cause.Phase),
                RequestBytes = requestBytes,
                UserMessage = cause.UserMessage,
                RemediationHint = cause.RemediationHint,
                Evidence = cause.Evidence,
            },
        };

        return IncidentProjector.Project(input.TurnId, facts);
    }

    private static string SerializeEvidence(IReadOnlyDictionary<string, string> evidence)
    {
        // 证据已在叶子组件内脱敏与限长（≤32 键 / 单值 ≤512 / 总量 ≤8 KiB），这里只做转义。
        var builder = new System.Text.StringBuilder("{");
        var first = true;
        foreach (var pair in evidence)
        {
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append(System.Text.Json.JsonSerializer.Serialize(pair.Key));
            builder.Append(':');
            builder.Append(System.Text.Json.JsonSerializer.Serialize(pair.Value));
        }

        return builder.Append('}').ToString();
    }
}
