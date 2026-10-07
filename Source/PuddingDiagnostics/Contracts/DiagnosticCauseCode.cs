namespace PuddingCode.Diagnostics;

/// <summary>
/// 稳定因果码（可诊断基础设施设计 §5.1）。
/// <para>
/// 这些字符串是**跨层契约**：日志、<c>runtime_activity</c> / <c>telemetry_metric_events</c> 元数据、
/// 终态诊断 DTO 与前端本地化都使用同一取值。新增取值必须同时补：① 目录条目
/// （<see cref="DiagnosticCauseCatalog"/>）；② 「故障场景 → 期望码」用例
/// （<c>PuddingDiagnosticsTests</c>）；否则门禁取红。
/// </para>
/// <para>
/// 不变量：码是**不可变语义**（"发生了什么"），不是异常类型名（"哪个 CLR 类型抛的"）。
/// 原始异常只作为证据（<see cref="DiagnosticEvidenceKeys.ExceptionChain"/>）。
/// </para>
/// </summary>
public static class DiagnosticCauseCode
{
    // ── 传输层（transport.*）─────────────────────────────────────────

    /// <summary>写请求体时连接被对端重置/关闭（2026-10-07 事故的确凿指纹：异常链含 HttpContent.CopyToAsync）。</summary>
    public const string RequestUploadReset = "transport.request_upload_reset";

    /// <summary>读响应体时连接被重置，或流提前结束（ResponseEnded）。</summary>
    public const string ResponseReadReset = "transport.response_read_reset";

    /// <summary>TCP 连接建立失败（拒绝/超时/不可达）。</summary>
    public const string ConnectFailed = "transport.connect_failed";

    /// <summary>域名解析失败。</summary>
    public const string DnsFailed = "transport.dns_failed";

    /// <summary>TLS 握手或证书校验失败。</summary>
    public const string TlsFailed = "transport.tls_failed";

    /// <summary>首块看门狗超时（模型在期限内没有产出任何块）。</summary>
    public const string FirstChunkTimeout = "transport.first_chunk_timeout";

    /// <summary>流中空闲看门狗超时（收到过块，之后长时间无块）。</summary>
    public const string StreamIdleTimeout = "transport.stream_idle_timeout";

    /// <summary>HttpClient 级超时 / 传输层取消（既非外部取消也非看门狗）。</summary>
    public const string HttpClientTimeout = "transport.http_client_timeout";

    /// <summary>传输失败但客户端拿不到足以定性的信号（诚实的兜底，不得伪装成具体原因）。</summary>
    public const string UnclassifiedTransport = "transport.unclassified_failure";

    // ── 服务商层（provider.*）────────────────────────────────────────

    public const string Http401Or403 = "provider.http_401_403";
    public const string Http400 = "provider.http_400";
    public const string Http404 = "provider.http_404";
    public const string Http429 = "provider.http_429";
    public const string Http5xx = "provider.http_5xx";

    /// <summary>协议层不合法：SSE/JSON 解析失败、路由协议与服务商返回不符。</summary>
    public const string ProtocolInvalid = "provider.protocol_invalid";

    /// <summary>请求体过大（本地闸门或服务商限制）。</summary>
    public const string RequestTooLarge = "provider.request_too_large";

    // ── 本地层（local.*）────────────────────────────────────────────

    /// <summary>外部取消（用户停止 / 父 Run 取消）。</summary>
    public const string Cancelled = "local.cancelled";

    /// <summary>熔断器打开，请求未发出。</summary>
    public const string CircuitOpen = "local.circuit_open";

    /// <summary>并发槽位等待阶段被取消。</summary>
    public const string RateLimitWaitCancelled = "local.rate_limit_wait_cancelled";

    /// <summary>本地失败但无既有语义码可用（诚实的兜底）。</summary>
    public const string UnclassifiedLocal = "local.unclassified_failure";

    // ── 视觉层（vision.*）────────────────────────────────────────────

    /// <summary>
    /// 视觉链路码前缀。取值由 <c>PuddingCode.Core.VisionErrorCodes</c> 定义（ADR-077 §9.1），
    /// 通过 <see cref="DiagnosticContext.CauseCodeOverride"/> 原样透传，不在本组件里复制一份码表。
    /// </summary>
    public const string VisionPrefix = "vision.";

    /// <summary>把任意码归类到稳定的类别名（用于聚合与 UI 分组）。</summary>
    public static string CategoryOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return DiagnosticCauseCategory.Unknown;

        var separator = code.IndexOf('.');
        var prefix = separator < 0 ? code : code[..separator];
        return prefix switch
        {
            "transport" => DiagnosticCauseCategory.Transport,
            "provider" => DiagnosticCauseCategory.Provider,
            "local" => DiagnosticCauseCategory.Local,
            "vision" => DiagnosticCauseCategory.Vision,
            _ => DiagnosticCauseCategory.Unknown,
        };
    }

    /// <summary>是否为已登记（有目录条目）的码。未登记的码说明码表与目录漂移。</summary>
    public static bool IsRegistered(string? code)
        => !string.IsNullOrWhiteSpace(code)
           && (DiagnosticCauseCatalog.Codes.Contains(code) || code.StartsWith(VisionPrefix, StringComparison.Ordinal));
}

/// <summary>稳定类别名。</summary>
public static class DiagnosticCauseCategory
{
    public const string Transport = "transport";
    public const string Provider = "provider";
    public const string Local = "local";
    public const string Vision = "vision";
    public const string Unknown = "unknown";
}
