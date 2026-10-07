using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingCode.Diagnostics;

/// <summary>
/// 「一键复制现场」的完整载荷（可诊断基础设施设计 §12）。
/// <para>
/// 界面只显示 <see cref="ErrorPresentation"/> 的简略信息；用户点「复制诊断信息」时，
/// 复制/下载的是本对象的 <see cref="RenderText"/>（人读+可直接贴给维护者）或
/// <see cref="RenderJson"/>（机器读）。
/// </para>
/// <para>
/// 必含定位要素：**捕获时间（UTC）**、`traceId`、`errorId`、`turnId`、`sessionId`、
/// 稳定因果码、失败阶段、尝试明细、证据，以及**日志文件定位提示**——目的是让维护者不必
/// 在茫茫日志里摸索，而是按 `errorId` / `traceId` 直接命中现场。
/// </para>
/// </summary>
public sealed record DiagnosticReportDocument
{
    public const string SchemaVersion = "pudding.diagnostic-error/1";

    /// <summary>渲染上限：超出即截断并显式标注（不允许静默变短）。</summary>
    public const int MaxRenderedChars = 64 * 1024;

    public string Schema { get; init; } = SchemaVersion;

    /// <summary>
    /// 捕获时刻。渲染时同时输出**原偏移（本地）**与 **UTC**：日志时间戳是本地时间，
    /// 而汇报给维护者需要无歧义的 UTC —— 两者都给，才不至于在现场对不上时间。
    /// </summary>
    public required DateTimeOffset CapturedAt { get; init; }

    public required ErrorPresentation Presentation { get; init; }

    public string? TerminalStatus { get; init; }
    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    public string? TraceId { get; init; }
    public string? ErrorId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? ExecutionId { get; init; }

    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? EndpointHost { get; init; }
    public string? PlatformVersion { get; init; }

    public string? CauseCode { get; init; }
    public string? CauseMessage { get; init; }
    public string? RemediationHint { get; init; }
    public DiagnosticPhaseKind Phase { get; init; }
    public IReadOnlyDictionary<string, string>? Evidence { get; init; }
    public IReadOnlyList<IncidentAttempt> Attempts { get; init; } = [];

    /// <summary>由一次事故投影生成报告载荷；证据取自终态事实。</summary>
    public static DiagnosticReportDocument FromIncident(
        IncidentView incident,
        DateTimeOffset capturedAt,
        IReadOnlyDictionary<string, string>? evidence = null,
        string? providerId = null,
        string? modelId = null,
        string? endpointHost = null,
        string? sessionId = null,
        string? workspaceId = null,
        string? agentTemplateId = null,
        string? agentInstanceId = null,
        string? executionId = null,
        string? platformVersion = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var code = incident.RootCauseCode ?? DiagnosticCauseCode.UnclassifiedLocal;
        var presentation = ErrorPresentationCatalog.Describe(code, retryable: false);

        return new DiagnosticReportDocument
        {
            CapturedAt = capturedAt,
            Presentation = presentation,
            TerminalStatus = incident.TerminalStatus,
            SessionId = sessionId,
            TurnId = incident.TurnId,
            TraceId = incident.TraceId,
            ErrorId = incident.ErrorId,
            WorkspaceId = workspaceId,
            AgentTemplateId = agentTemplateId,
            AgentInstanceId = agentInstanceId,
            ExecutionId = executionId,
            ProviderId = providerId,
            ModelId = modelId,
            EndpointHost = endpointHost,
            PlatformVersion = platformVersion,
            CauseCode = incident.RootCauseCode,
            CauseMessage = incident.RootCauseMessage,
            RemediationHint = incident.RemediationHint,
            Phase = incident.Phase,
            Evidence = evidence,
            Attempts = incident.Attempts,
        };
    }

    /// <summary>
    /// 日志定位提示：按真实目录布局给出「去哪找、搜什么」。
    /// 这是把「复制一段报错」变成「复制一条可定位的线索」的关键。
    /// </summary>
    public IReadOnlyList<string> BuildLogHints()
    {
        var hints = new List<string>();

        if (!string.IsNullOrWhiteSpace(ErrorId))
        {
            hints.Add($"<DataRoot>/logs/error/pudding-error-*.log：搜 errorId={ErrorId}");
            hints.Add($"<DataRoot>/logs/system/pudding-*.log：搜 errorId={ErrorId}");
        }

        if (!string.IsNullOrWhiteSpace(TraceId))
            hints.Add($"<DataRoot>/logs/components/llm_gateway/：搜 traceId={TraceId}");

        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            hints.Add($"<DataRoot>/logs/diagnostics/session-timeline/：按 {SessionId} 取当日 JSONL");
            hints.Add($"<DataRoot>/logs/sessions/：按 {SessionId} 取会话归档");
        }

        hints.Add("<DataRoot>/logs/components/agent_execution/：搜 \"[AgentExec] LLM API error\"");

        if (!string.IsNullOrWhiteSpace(TurnId))
            hints.Add($"会话事件表 conversation_events：按 turn_id={TurnId} 取终态事件");

        return hints;
    }

    /// <summary>人读/可直接粘贴的文本（确定性：同输入同输出）。</summary>
    public string RenderText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"== Pudding 错误报告（{Schema}） ==");
        builder.AppendLine($"捕获时间(本地): {CapturedAt:O}");
        builder.AppendLine($"捕获时间(UTC): {CapturedAt.ToUniversalTime():O}");
        builder.AppendLine($"标题: {Presentation.Title}");
        builder.AppendLine($"大概原因: {Presentation.ShortCause}");
        builder.AppendLine($"可重试: {(Presentation.Retryable ? "是" : "否")}    建议动作: {Presentation.PrimaryAction}");
        builder.AppendLine();
        builder.AppendLine("-- 定位要素 --");
        builder.AppendLine($"错误码: {CauseCode ?? Presentation.CauseCode}");
        builder.AppendLine($"类别: {Presentation.Category}");
        builder.AppendLine($"失败阶段: {DiagnosticPhases.Label(Phase)}（{DiagnosticPhases.ToWire(Phase)}）");
        builder.AppendLine($"errorId: {Value(ErrorId)}");
        builder.AppendLine($"traceId: {Value(TraceId)}");
        builder.AppendLine($"turnId: {Value(TurnId)}");
        builder.AppendLine($"sessionId: {Value(SessionId)}");
        builder.AppendLine($"executionId: {Value(ExecutionId)}");
        builder.AppendLine($"workspaceId: {Value(WorkspaceId)}");
        builder.AppendLine($"agent: template={Value(AgentTemplateId)} instance={Value(AgentInstanceId)}");
        builder.AppendLine($"终态: {Value(TerminalStatus)}");
        builder.AppendLine($"provider/model: {Value(ProviderId)} / {Value(ModelId)}");
        builder.AppendLine($"endpoint: {Value(EndpointHost)}");
        builder.AppendLine($"platform: {Value(PlatformVersion)}");
        builder.AppendLine();

        builder.AppendLine($"-- 尝试明细（{Attempts.Count} 次）--");
        if (Attempts.Count == 0)
        {
            builder.AppendLine("（未记录到尝试明细）");
        }
        else
        {
            foreach (var attempt in Attempts)
            {
                builder.AppendLine(
                    $"attempt={Value(attempt.Attempt)} status={attempt.Status} phase={attempt.Phase ?? DiagnosticPhases.UnknownWire} " +
                    $"cause={Value(attempt.CauseCode)} durationMs={Value(attempt.DurationMs)} " +
                    $"requestBytes={Value(attempt.RequestBytes)} at={attempt.OccurredAtUtc:O}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("-- 证据 --");
        if (Evidence is null || Evidence.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var pair in Evidence)
            {
                if (string.Equals(pair.Key, DiagnosticEvidenceKeys.ExceptionChain, StringComparison.Ordinal))
                    continue;
                builder.AppendLine($"{pair.Key}={pair.Value}");
            }

            if (Evidence.TryGetValue(DiagnosticEvidenceKeys.ExceptionChain, out var chain))
            {
                builder.AppendLine();
                builder.AppendLine("-- 异常链 --");
                builder.AppendLine(chain);
            }
        }

        if (!string.IsNullOrWhiteSpace(CauseMessage))
        {
            builder.AppendLine();
            builder.AppendLine("-- 原因说明 --");
            builder.AppendLine(CauseMessage);
        }

        if (!string.IsNullOrWhiteSpace(RemediationHint))
        {
            builder.AppendLine();
            builder.AppendLine("-- 处置建议 --");
            builder.AppendLine(RemediationHint);
        }

        builder.AppendLine();
        builder.AppendLine("-- 日志定位 --");
        foreach (var hint in BuildLogHints())
            builder.AppendLine($"- {hint}");

        return Clamp(builder.ToString());
    }

    /// <summary>机器读的 JSON（缩进、枚举按字符串、证据键序稳定）。</summary>
    public string RenderJson()
        => Clamp(JsonSerializer.Serialize(this, JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>超限即截断并显式标注；复制出去的字符串永远不会「悄悄变短」。</summary>
    private static string Clamp(string text)
        => text.Length <= MaxRenderedChars
            ? text
            : text[..MaxRenderedChars] + $"\n…[报告已截断：原始长度 {text.Length} 字符，上限 {MaxRenderedChars}]";

    private static string Value(object? value) => value switch
    {
        null => "(未提供)",
        string text when string.IsNullOrWhiteSpace(text) => "(未提供)",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(未提供)",
    };
}
