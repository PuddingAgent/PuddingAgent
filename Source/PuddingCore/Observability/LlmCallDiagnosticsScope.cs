using System.Diagnostics;
using PuddingCode.Diagnostics;

namespace PuddingCode.Observability;

/// <summary>
/// 单次 LLM 调用尝试的**诊断事实收集器**（可诊断基础设施设计 §7 Stage 3）。
/// <para>
/// 存在理由：请求体字节数、真实 HTTP 派发次数、是否已收到响应头、失败发生在哪个阶段 ——
/// 这些事实**只有网关知道**，而调用方（`DirectLlmClient`）才持有异常与重试语义。
/// 用 <see cref="AsyncLocal{T}"/> 做一条**同一调用内**的单向通道：调用方 <see cref="Begin"/>，
/// 网关侧 <c>MarkDispatch/MarkHeaders/MarkReadStream</c> 写入，调用方在分类时读取。
/// </para>
/// <para>
/// 边界：它只是**事实容器**（可变字段 + 计数器），不含分类逻辑、不做 IO、不写日志；
/// 分类由叶子组件 <c>PuddingCode.Diagnostics</c> 负责。并发/嵌套调用通过 <see cref="IDisposable"/>
/// 恢复上一作用域，互不串台。
/// </para>
/// </summary>
public sealed class LlmCallDiagnosticsScope : IDisposable
{
    private static readonly AsyncLocal<LlmCallDiagnosticsScope?> Ambient = new();
    private readonly LlmCallDiagnosticsScope? _parent;
    private long? _dispatchStartedTimestamp;

    private LlmCallDiagnosticsScope()
    {
        _parent = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>当前作用域；未开启时为 null（网关侧的写入一律走空合并，不强制要求）。</summary>
    public static LlmCallDiagnosticsScope? Current => Ambient.Value;

    public static LlmCallDiagnosticsScope Begin() => new();

    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public string? EndpointHost { get; set; }

    /// <summary>本次尝试序号（1 起）。</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>允许的额外重试次数。</summary>
    public int MaxRetries { get; set; }

    /// <summary>最后一次派发的请求体字节数（静默重发时取最后一次，即真正失败的那次）。</summary>
    public long? RequestBytes { get; private set; }

    /// <summary>真实 HTTP 派发次数（含非流式→流式回退、文件引用过期重建等静默重发）。</summary>
    public int DispatchCount { get; private set; }

    public bool ResponseHeadersReceived { get; private set; }

    public DiagnosticPhaseKind Phase { get; private set; } = DiagnosticPhaseKind.Unknown;

    /// <summary>最近一次派发到现在的耗时（用于区分「上传慢」与「服务端不回」）。</summary>
    public long? SinceDispatchMs => _dispatchStartedTimestamp is { } started
        ? (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
        : null;

    // ── 调用方填写的现场标志（决定分类走哪条规则）──
    public bool HasYieldedDelta { get; set; }
    public bool UserCancelled { get; set; }
    public bool FirstChunkTimeout { get; set; }
    public bool StreamIdleTimeout { get; set; }
    public bool HttpClientTimeout { get; set; }

    // ── 网关可以直接指定的稳定码（例如本地体积闸门、服务商明确错误）──
    public string? CauseCodeOverride { get; set; }
    public bool? OverrideRetryable { get; set; }
    public DiagnosticPhaseKind? OverridePhase { get; set; }

    /// <summary>派发请求体：记录字节数、派发次数与阶段。</summary>
    public void MarkDispatch(long? requestBytes)
    {
        if (requestBytes is { } bytes)
            RequestBytes = bytes;

        DispatchCount++;
        Phase = DiagnosticPhaseKind.RequestUpload;
        _dispatchStartedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>收到响应头：此后任何传输失败都算「读响应流」阶段，而不是上传阶段。</summary>
    public void MarkHeaders()
    {
        ResponseHeadersReceived = true;
        Phase = DiagnosticPhaseKind.ReadResponseStream;
    }

    /// <summary>开始读响应流（响应头已到但尚未读取时调用）。</summary>
    public void MarkReadStream() => Phase = DiagnosticPhaseKind.ReadResponseStream;

    /// <summary>组装/序列化阶段（构造请求体时调用，用于把阶段错误归位）。</summary>
    public void MarkSerialize() => Phase = DiagnosticPhaseKind.Serialize;

    public void Dispose()
    {
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = _parent;
    }

    /// <summary>把收集到的事实折成分类器输入（不含异常本身）。</summary>
    public DiagnosticContext ToContext(int? attempt = null, int? maxRetries = null, long? failureAfterMs = null,
        int? messageCount = null, int? toolCount = null)
        => new()
        {
            ProviderId = ProviderId,
            ModelId = ModelId,
            EndpointHost = EndpointHost,
            Attempt = attempt ?? Attempt,
            MaxRetries = maxRetries ?? MaxRetries,
            DispatchCount = DispatchCount == 0 ? null : DispatchCount,
            RequestBytes = RequestBytes,
            ResponseHeadersReceived = DispatchCount == 0 ? null : ResponseHeadersReceived,
            PhaseHint = Phase,
            SinceDispatchMs = DispatchCount == 0 ? null : SinceDispatchMs,
            FailureAfterMs = failureAfterMs,
            MessageCount = messageCount,
            ToolCount = toolCount,
            HasYieldedDelta = HasYieldedDelta,
            UserCancelled = UserCancelled,
            FirstChunkTimeout = FirstChunkTimeout,
            StreamIdleTimeout = StreamIdleTimeout,
            HttpClientTimeout = HttpClientTimeout,
            CauseCodeOverride = CauseCodeOverride,
            OverrideRetryable = OverrideRetryable,
            OverridePhase = OverridePhase,
        };

    /// <summary>从端点字符串安全取主机名（绝不把完整 URL 或 query 放进事实里）。</summary>
    public static string? HostOf(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : null;
    }
}
