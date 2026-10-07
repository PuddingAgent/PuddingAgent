using PuddingCode.Diagnostics;
using PuddingCode.Observability;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 侧的因果分类适配（可诊断基础设施设计 §7 Stage 3）。
/// <para>
/// 职责边界：叶子组件只做「异常 + 现场 → 稳定因果结论」；本适配器负责把
/// <see cref="LlmCallDiagnosticsScope"/> 收集到的现场折成分类器输入，
/// 并把结论摊平成**可落库的活动元数据键**（键名与终态 DTO、前端呈现共用同一套语义）。
/// </para>
/// </summary>
internal static class LlmFailureDiagnostics
{
    /// <summary>活动元数据键（与叶子组件的证据键同名同义，便于一条查询串起来）。</summary>
    public const string CauseCodeMetadataKey = "cause_code";
    public const string CauseCategoryMetadataKey = "cause_category";
    public const string CausePhaseMetadataKey = "cause_phase";
    public const string CauseRetryableMetadataKey = "cause_retryable";
    public const string CauseRuleMetadataKey = "cause_rule";
    public const string CauseMessageMetadataKey = "cause_message";
    public const string RemediationMetadataKey = "cause_remediation";
    public const string RequestBytesMetadataKey = DiagnosticEvidenceKeys.RequestBytes;
    public const string AttemptMetadataKey = DiagnosticEvidenceKeys.Attempt;
    public const string MaxRetriesMetadataKey = DiagnosticEvidenceKeys.MaxRetries;
    public const string DispatchCountMetadataKey = DiagnosticEvidenceKeys.DispatchCount;
    public const string ResponseHeadersMetadataKey = DiagnosticEvidenceKeys.ResponseHeadersReceived;
    public const string SocketErrorMetadataKey = DiagnosticEvidenceKeys.SocketErrorCode;

    private static readonly IDiagnosticCauseClassifier Classifier = LlmFailureClassifier.Default;

    /// <summary>对一次失败做因果分类；现场来自当前作用域（可能为空）。</summary>
    public static DiagnosticCause Classify(
        Exception error,
        LlmCallDiagnosticsScope? scope,
        int? messageCount = null,
        int? toolCount = null,
        long? failureAfterMs = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var context = scope?.ToContext(messageCount: messageCount, toolCount: toolCount, failureAfterMs: failureAfterMs)
                      ?? new DiagnosticContext
                      {
                          MessageCount = messageCount,
                          ToolCount = toolCount,
                          FailureAfterMs = failureAfterMs,
                      };

        return Classifier.Classify(error, context);
    }

    /// <summary>把因果结论摊平成活动元数据（在原有元数据之上合并；失败元数据优先）。</summary>
    public static IReadOnlyDictionary<string, string> MergeInto(
        IReadOnlyDictionary<string, string>? metadata,
        DiagnosticCause cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        var merged = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        merged[CauseCodeMetadataKey] = cause.Code;
        merged[CauseCategoryMetadataKey] = cause.Category;
        merged[CausePhaseMetadataKey] = DiagnosticPhases.ToWire(cause.Phase);
        merged[CauseRetryableMetadataKey] = cause.Retryable ? "true" : "false";
        merged[CauseRuleMetadataKey] = cause.Rule;
        merged[CauseMessageMetadataKey] = cause.UserMessage;
        merged[RemediationMetadataKey] = cause.RemediationHint;

        foreach (var key in new[]
                 {
                     RequestBytesMetadataKey, AttemptMetadataKey, MaxRetriesMetadataKey,
                     DispatchCountMetadataKey, ResponseHeadersMetadataKey, SocketErrorMetadataKey,
                 })
        {
            if (cause.Evidence.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                merged[key] = value;
        }

        return merged;
    }

    /// <summary>面向界面/复制的完整结论（含标题、大概原因、原始异常链证据）。</summary>
    public static (ErrorPresentation Presentation, DiagnosticCause Cause) Present(Exception error, LlmCallDiagnosticsScope? scope)
    {
        var cause = Classify(error, scope);
        return (ErrorPresentationCatalog.Present(cause), cause);
    }
}
