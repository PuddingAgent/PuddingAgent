namespace PuddingCode.Diagnostics;

/// <summary>
/// 一次失败的**因果结论**（可诊断基础设施设计 §5.2）。
/// <para>
/// 这是跨层传递的稳定形状：后端日志/活动元数据、终态诊断 DTO、前端本地化都读同一组字段。
/// <see cref="UserMessage"/> 与 <see cref="RemediationHint"/> 必须是可直接展示的中文，
/// 不允许把原始异常字符串塞进来当结论（原始异常走 <see cref="Evidence"/>）。
/// </para>
/// </summary>
public sealed record DiagnosticCause
{
    /// <summary>稳定码，取值见 <see cref="DiagnosticCauseCode"/>。</summary>
    public required string Code { get; init; }

    /// <summary>稳定类别，取值见 <see cref="DiagnosticCauseCategory"/>。</summary>
    public required string Category { get; init; }

    /// <summary>
    /// 是否可重试。**已结合「是否已产出任何增量」判定**：一旦产出正文/思考/工具增量，
    /// 传输类与服务商瞬态类都会降级为不可重试（Docs/08_how_debuge/06-延迟问题定位.md §7.9）。
    /// </summary>
    public required bool Retryable { get; init; }

    /// <summary>失败阶段。</summary>
    public required DiagnosticPhaseKind Phase { get; init; }

    /// <summary>面向用户的中文短消息（可直接进聊天终态）。</summary>
    public required string UserMessage { get; init; }

    /// <summary>处置建议（下一步该做什么）。</summary>
    public required string RemediationHint { get; init; }

    /// <summary>有界证据（键值均为稳定字符串，键名见 <see cref="DiagnosticEvidenceKeys"/>）。</summary>
    public required IReadOnlyDictionary<string, string> Evidence { get; init; }

    /// <summary>命中的分类规则名（用于诊断分类器自身；出现意外取值时说明规则缺失）。</summary>
    public required string Rule { get; init; }
}

/// <summary>码表条目：稳定码 → 可重试性 / 用户消息 / 处置建议。</summary>
public sealed record DiagnosticCauseDescriptor(
    string Code,
    bool RetryableByDefault,
    string UserMessage,
    string RemediationHint);

/// <summary>
/// 稳定码目录（唯一真源）。分类器、日志与消费方都从这里取用户消息；
/// 消费方若只拿到码（例如从库里读到历史事实），也必须能还原完整语义。
/// </summary>
public static class DiagnosticCauseCatalog
{
    private static readonly DiagnosticCauseDescriptor[] All =
    [
        new(DiagnosticCauseCode.RequestUploadReset, true,
            "请求体上传阶段连接被对端重置（网络或代理不稳定）。",
            "先确认网络/代理路径（可做一次开/关代理的对比），再重试；反复出现时同时减小上下文与工具集体积。"),
        new(DiagnosticCauseCode.ResponseReadReset, true,
            "响应读取阶段连接中断，模型输出不完整。",
            "直接重试；若与特定长会话强相关，检查链路 MTU/代理与上下文体积。"),
        new(DiagnosticCauseCode.ConnectFailed, true,
            "无法连接到模型服务端点。",
            "检查网络连通性、端点地址与防火墙后重试。"),
        new(DiagnosticCauseCode.DnsFailed, true,
            "模型服务域名解析失败。",
            "检查 DNS 设置与网络环境后重试。"),
        new(DiagnosticCauseCode.TlsFailed, false,
            "与模型服务的 TLS 握手失败（证书或中间代理问题）。",
            "检查代理/证书配置；这类失败重试通常无效。"),
        new(DiagnosticCauseCode.FirstChunkTimeout, true,
            "模型在首块期限内没有返回任何内容。",
            "重试；若持续发生，降低上下文体积或改用响应更快的模型。"),
        new(DiagnosticCauseCode.StreamIdleTimeout, false,
            "模型输出中途停止（长时间没有新内容）。",
            "已产出内容的流不重试以免重复输出；可重新发起一轮对话。"),
        new(DiagnosticCauseCode.HttpClientTimeout, true,
            "模型调用超时。",
            "重试；持续超时时检查链路质量与请求体积。"),
        new(DiagnosticCauseCode.UnclassifiedTransport, true,
            "模型调用在传输层失败（现有信号不足以定性）。",
            "重试一次并保留本次证据；若反复出现，按 errorId 归档日志补充判读规则。"),
        new(DiagnosticCauseCode.Http401Or403, false,
            "模型服务拒绝鉴权（密钥或权限问题）。",
            "检查服务商密钥与模型权限；重试无效。"),
        new(DiagnosticCauseCode.Http400, false,
            "模型服务拒绝了本次请求（参数或协议不合法）。",
            "检查模型/协议配置与请求格式；重试无效。"),
        new(DiagnosticCauseCode.Http404, false,
            "模型服务端点或模型不存在。",
            "核对服务商 baseUrl 与模型名；重试无效。"),
        new(DiagnosticCauseCode.Http429, true,
            "模型服务限流。",
            "退避后重试；持续限流时降低并发或改用其他模型。"),
        new(DiagnosticCauseCode.Http5xx, true,
            "模型服务故障（服务端错误）。",
            "退避重试；持续故障时切换服务商或稍后再试。"),
        new(DiagnosticCauseCode.ProtocolInvalid, false,
            "模型返回的数据不符合协议（流解析失败）。",
            "核对 model.protocol 与服务商端点协议；重试无效。"),
        new(DiagnosticCauseCode.RequestTooLarge, false,
            "请求体超过限制。",
            "精简上下文/图片或改用文件引用后再试；重试无效。"),
        new(DiagnosticCauseCode.Cancelled, false,
            "调用已被取消。",
            "无需处理；如需继续请重新发起。"),
        new(DiagnosticCauseCode.CircuitOpen, true,
            "该服务商处于熔断状态，请求未发出。",
            "等待恢复窗口结束或切换服务商后再试。"),
        new(DiagnosticCauseCode.RateLimitWaitCancelled, false,
            "等待并发槽位时被取消。",
            "降低并发或稍后重试。"),
        new(DiagnosticCauseCode.UnclassifiedLocal, false,
            "模型调用失败（本地未分类原因）。",
            "按 errorId 归档证据；补充分类规则后重试。"),
    ];

    private static readonly IReadOnlyDictionary<string, DiagnosticCauseDescriptor> ByCode =
        All.ToDictionary(d => d.Code, StringComparer.Ordinal);

    /// <summary>全部已登记码（用于门禁断言「码表无漂移」）。</summary>
    public static IReadOnlyCollection<string> Codes { get; } = All.Select(descriptor => descriptor.Code).ToArray();

    public static bool TryGet(string? code, out DiagnosticCauseDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = Fallback(code);
        return false;
    }

    /// <summary>
    /// 未登记码（例如 <c>vision.*</c> 透传码、或上游新引入的码）的诚实兜底：
    /// 不猜语义，不宣称可重试。
    /// </summary>
    public static DiagnosticCauseDescriptor Describe(string? code)
        => TryGet(code, out var descriptor) ? descriptor : descriptor;

    private static DiagnosticCauseDescriptor Fallback(string? code)
        => code is not null && code.StartsWith(DiagnosticCauseCode.VisionPrefix, StringComparison.Ordinal)
            ? new DiagnosticCauseDescriptor(code, false,
                "本次图片处理链路失败。",
                "检查图片/来源可访问性；文字对话不受影响。")
            : new DiagnosticCauseDescriptor(
                string.IsNullOrWhiteSpace(code) ? DiagnosticCauseCode.UnclassifiedLocal : code,
                false,
                "模型调用失败（未登记的失败种类）。",
                "按 errorId 归档证据并补充分类规则；重试通常无效。");
}
