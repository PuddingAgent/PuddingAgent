namespace PuddingCode.Diagnostics;

/// <summary>证据键名（稳定字符串；落库、日志、前端共用同一组键）。</summary>
public static class DiagnosticEvidenceKeys
{
    public const string ProviderId = "provider_id";
    public const string ModelId = "model_id";
    public const string EndpointHost = "endpoint_host";
    public const string Attempt = "attempt";
    public const string MaxRetries = "max_retries";
    public const string DispatchCount = "dispatch_count";
    public const string Phase = "phase";
    public const string PhaseDetection = "phase_detection";
    public const string FailureAfterMs = "failure_after_ms";
    public const string SinceDispatchMs = "since_dispatch_ms";
    public const string ResponseHeadersReceived = "response_headers_received";
    public const string RequestBytes = "request_bytes";
    public const string MessageCount = "message_count";
    public const string ToolCount = "tool_count";
    public const string HasYieldedDelta = "has_yielded_delta";
    public const string Retryable = "retryable";
    public const string Rule = "rule";
    public const string SocketErrorCode = "socket_error_code";
    public const string HttpStatus = "http_status";
    public const string ExceptionChain = "exception_chain";
    public const string CancelReason = "cancel_reason";
    public const string EvidenceTruncated = "evidence_truncated";
}

/// <summary>
/// 有界证据构建器（可诊断基础设施设计 §5.4）。
/// <para>
/// 三条硬约束，全部由用例锁住：
/// ① 键数 ≤ <see cref="MaxKeys"/>；
/// ② 单值 ≤ <see cref="MaxValueChars"/>（超出加 <c>…[truncated]</c>）；
/// ③ 总量 ≤ <see cref="MaxTotalChars"/>，触顶后停止收集并写入
///    <see cref="DiagnosticEvidenceKeys.EvidenceTruncated"/>=true（**不静默丢弃**）。
/// 另外：所有值在入口处过 <see cref="DiagnosticEvidenceRedactor"/>，因此脱敏不可能被某个调用点绕过。
/// 键名与输出顺序都是 ordinal 排序，保证落库/断言可复现。
/// </para>
/// </summary>
public sealed class DiagnosticEvidenceBuilder
{
    public const int MaxKeys = 32;
    public const int MaxValueChars = DiagnosticEvidenceRedactor.MaxValueChars;
    public const int MaxTotalChars = 8192;

    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);
    private int _totalChars;
    private bool _truncated;

    /// <summary>已收集的键数（不含自动追加的截断标记）。</summary>
    public int Count => _values.Count;

    public bool IsTruncated => _truncated;

    /// <summary>首个值生效：同一键重复添加不覆盖（避免后写的空值抹掉先写的实测值）。</summary>
    public DiagnosticEvidenceBuilder Add(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value) || _truncated)
            return this;

        if (_values.ContainsKey(key))
            return this;

        if (_values.Count >= MaxKeys)
            return MarkTruncated();

        var sanitized = DiagnosticEvidenceRedactor.Sanitize(key, value);
        if (sanitized.Length == 0)
            return this;

        if (_totalChars + sanitized.Length > MaxTotalChars)
            return MarkTruncated();

        _values[key] = sanitized;
        _totalChars += sanitized.Length;
        return this;
    }

    public DiagnosticEvidenceBuilder Add(string key, long? value)
        => value.HasValue ? Add(key, value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : this;

    public DiagnosticEvidenceBuilder Add(string key, int? value)
        => value.HasValue ? Add(key, value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : this;

    public DiagnosticEvidenceBuilder Add(string key, bool? value)
        => value.HasValue ? Add(key, value.Value ? "true" : "false") : this;

    public DiagnosticEvidenceBuilder AddIf(bool condition, string key, string? value)
        => condition ? Add(key, value) : this;

    public IReadOnlyDictionary<string, string> Build()
    {
        if (_truncated)
            _values[DiagnosticEvidenceKeys.EvidenceTruncated] = "true";

        return new SortedDictionary<string, string>(_values, StringComparer.Ordinal);
    }

    private DiagnosticEvidenceBuilder MarkTruncated()
    {
        _truncated = true;
        return this;
    }
}
