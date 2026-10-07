namespace PuddingCode.Diagnostics;

/// <summary>
/// 分类器的输入：**调用现场可观测到的一切**（可诊断基础设施设计 §5.3/§5.4）。
/// <para>
/// 消费方（网关 / DirectLlmClient / Agent 执行）负责填充本记录；分类器只做纯计算。
/// 字段都可选，缺省即「未知」——未知会在证据里如实标出，不允许用猜测填充。
/// </para>
/// </summary>
public sealed record DiagnosticContext
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? EndpointHost { get; init; }

    /// <summary>本次尝试序号（1 起）。</summary>
    public int? Attempt { get; init; }

    /// <summary>允许的额外重试次数（不含首次尝试）。</summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// 消费方对阶段的先验判断（例如网关知道自己是在 <c>SendAsync</c> 里失败，
    /// 还是读取 SSE 时失败）。分类器在异常链没有阶段指纹时才采用它。
    /// </summary>
    public DiagnosticPhaseKind PhaseHint { get; init; } = DiagnosticPhaseKind.Unknown;

    /// <summary>本次尝试从开始到失败的耗时。</summary>
    public long? FailureAfterMs { get; init; }

    /// <summary>
    /// 从「请求体已构造完毕、准备写出」到失败的耗时（网关侧
    /// <c>ProviderStreamTiming.MarkDispatch</c> → 失败）；用于区分「上传慢」与「服务端不回」。
    /// </summary>
    public long? SinceDispatchMs { get; init; }

    /// <summary>是否已收到响应头。</summary>
    public bool? ResponseHeadersReceived { get; init; }

    /// <summary>序列化后的请求体字节数（不含 HTTP 头）。</summary>
    public long? RequestBytes { get; init; }

    public int? MessageCount { get; init; }
    public int? ToolCount { get; init; }

    /// <summary>
    /// 本次调用实际向服务商发出的 HTTP 请求次数（含静默重发：非流式→流式回退、
    /// 文件引用过期重建）。默认为 1；用于发现「静默重发」导致的尝试计数失真。
    /// </summary>
    public int? DispatchCount { get; init; }

    /// <summary>失败发生时是否已经产出过任何增量（正文/思考/工具参数）。</summary>
    public bool HasYieldedDelta { get; init; }

    /// <summary>外部取消（用户停止 / 父 Run 取消）。</summary>
    public bool UserCancelled { get; init; }

    /// <summary>首块看门狗触发。</summary>
    public bool FirstChunkTimeout { get; init; }

    /// <summary>流中空闲看门狗触发。</summary>
    public bool StreamIdleTimeout { get; init; }

    /// <summary>HttpClient 级超时 / 传输层取消。</summary>
    public bool HttpClientTimeout { get; init; }

    /// <summary>
    /// 由消费方直接指定的稳定码（例如 <c>vision.*</c> 透传、熔断、限流等待取消）。
    /// 设置后分类器不再猜测，只补齐类别/阶段/消息/证据。
    /// </summary>
    public string? CauseCodeOverride { get; init; }

    /// <summary>透传码的可重试性（消费方最清楚；缺省按「不可重试」处理）。</summary>
    public bool? OverrideRetryable { get; init; }

    /// <summary>透传码的阶段（缺省沿用 <see cref="PhaseHint"/>）。</summary>
    public DiagnosticPhaseKind? OverridePhase { get; init; }
}
