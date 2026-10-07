using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace PuddingCode.Diagnostics;

/// <summary>
/// 故障场景表（可诊断基础设施设计 §6）。
/// <para>
/// 异常链按**生产实测形状**构造：例如 <c>reset_during_request_upload</c> 复刻 2026-10-07 的
/// <c>HttpRequestException("Error while copying content to a stream.")</c> →
/// <c>IOException</c> → <c>SocketException(10054)</c>。
/// </para>
/// <para>
/// 帧指纹场景（<see cref="UploadResetScenarioName"/>）无法在测试里伪造框架帧，因此该场景由
/// **注入指纹名**的独立用例覆盖（见 <c>UploadFrameFingerprintScenario</c> 的说明）。
/// </para>
/// </summary>
public static class FaultScenarios
{
    public const string UploadResetScenarioName = "reset_during_request_upload";

    /// <summary>默认上下文（未产出增量、未知端点等）；各场景按需覆盖。</summary>
    private static readonly DiagnosticContext Baseline = new()
    {
        ProviderId = "deepseek",
        ModelId = "deepseek-flash",
        EndpointHost = "api.deepseek.com",
        Attempt = 3,
        MaxRetries = 2,
        DispatchCount = 1,
        FailureAfterMs = 20093,
        SinceDispatchMs = 20093,
        RequestBytes = 1_048_576,
        MessageCount = 154,
        ToolCount = 99,
    };

    /// <summary>除帧指纹场景外的全部场景（分类器按默认指纹工作）。</summary>
    public static IReadOnlyList<FaultScenario> All { get; } =
    [
        new("reset_during_response_read", DiagnosticCauseCode.ResponseReadReset,
            DiagnosticPhaseKind.ReadResponseStream, true, null, Baseline,
            () => new HttpRequestException("transport", new HttpIOException(HttpRequestError.ResponseEnded, "response ended")),
            "Docs/08_how_debuge/06-延迟问题定位.md#79-responseended-后-agent-联系人持续显示异常"),

        new("tls_handshake_failure", DiagnosticCauseCode.TlsFailed,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, null, Baseline,
            () => new HttpRequestException("tls", new System.Security.Authentication.AuthenticationException("handshake failed")),
            "Docs/08_how_debuge/05-常见症状.md"),

        new("dns_failure", DiagnosticCauseCode.DnsFailed,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, null, Baseline,
            () => new HttpRequestException("dns", new SocketException(11001)),
            "Docs/08_how_debuge/05-常见症状.md"),

        new("connection_refused", DiagnosticCauseCode.ConnectFailed,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, null, Baseline,
            () => new HttpRequestException("refused", new SocketException(10061)),
            "Docs/08_how_debuge/05-常见症状.md"),

        new("http_401", DiagnosticCauseCode.Http401Or403,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, 401, Baseline,
            () => new HttpRequestException("unauthorized", inner: null, HttpStatusCode.Unauthorized),
            "Docs/08_how_debuge/05-常见症状.md"),

        new("http_429", DiagnosticCauseCode.Http429,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, 429, Baseline,
            () => new HttpRequestException("too many requests", inner: null, HttpStatusCode.TooManyRequests),
            "Docs/08_how_debuge/06-延迟问题定位.md"),

        new("http_503", DiagnosticCauseCode.Http5xx,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, 503, Baseline,
            () => new HttpRequestException("unavailable", inner: null, HttpStatusCode.ServiceUnavailable),
            "Docs/08_how_debuge/06-延迟问题定位.md"),

        new("http_400", DiagnosticCauseCode.Http400,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, 400, Baseline,
            () => new HttpRequestException("bad request", inner: null, HttpStatusCode.BadRequest),
            "Docs/08_how_debuge/12-案例-平台与会话.md"),

        new("http_404", DiagnosticCauseCode.Http404,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, 404, Baseline,
            () => new HttpRequestException("not found", inner: null, HttpStatusCode.NotFound),
            "Docs/08_how_debuge/12-案例-平台与会话.md"),

        new("http_413", DiagnosticCauseCode.RequestTooLarge,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, 413, Baseline,
            () => new HttpRequestException("payload too large", inner: null, HttpStatusCode.RequestEntityTooLarge),
            "Docs/12_features/可诊断基础设施设计-2026-10-07.md"),

        new("sse_parse_failure", DiagnosticCauseCode.ProtocolInvalid,
            DiagnosticPhaseKind.ReadResponseStream, false, null, Baseline,
            () => new JsonException("invalid SSE payload"),
            "Docs/08_how_debuge/12-案例-平台与会话.md"),

        new("first_chunk_timeout", DiagnosticCauseCode.FirstChunkTimeout,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, null, Baseline with { FirstChunkTimeout = true },
            () => new TimeoutException("LLM stream produced no first chunk before the watchdog deadline."),
            "Docs/08_how_debuge/06-延迟问题定位.md#78-长任务应继续运行还是已经停滞"),

        new("stream_idle_timeout", DiagnosticCauseCode.StreamIdleTimeout,
            DiagnosticPhaseKind.ReadResponseStream, false, null,
            Baseline with { StreamIdleTimeout = true, ResponseHeadersReceived = true },
            () => new TimeoutException("LLM stream stopped producing chunks before the watchdog deadline."),
            "Docs/08_how_debuge/06-延迟问题定位.md#78-长任务应继续运行还是已经停滞"),

        new("http_client_timeout", DiagnosticCauseCode.HttpClientTimeout,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, null, Baseline with { HttpClientTimeout = true },
            () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            "Docs/08_how_debuge/06-延迟问题定位.md"),

        new("external_cancellation", DiagnosticCauseCode.Cancelled,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, null, Baseline with { UserCancelled = true },
            () => new OperationCanceledException("user stopped"),
            "Docs/08_how_debuge/05-常见症状.md"),

        new("circuit_open_override", DiagnosticCauseCode.CircuitOpen,
            DiagnosticPhaseKind.Build, true, null,
            Baseline with { CauseCodeOverride = DiagnosticCauseCode.CircuitOpen, OverrideRetryable = true, OverridePhase = DiagnosticPhaseKind.Build },
            () => new InvalidOperationException("Circuit breaker is open for provider 'deepseek'."),
            "Docs/08_how_debuge/06-延迟问题定位.md"),

        new("vision_override", "vision_request_limit_exceeded",
            DiagnosticPhaseKind.Serialize, false, null,
            Baseline with { CauseCodeOverride = "vision_request_limit_exceeded", OverridePhase = DiagnosticPhaseKind.Serialize },
            () => new InvalidOperationException("DeepSeek request body exceeds 48 MiB. Preprocess images or use Files API references."),
            "Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md"),

        new("rate_limit_wait_cancelled", DiagnosticCauseCode.RateLimitWaitCancelled,
            DiagnosticPhaseKind.Queued, false, null,
            Baseline with
            {
                CauseCodeOverride = DiagnosticCauseCode.RateLimitWaitCancelled,
                OverridePhase = DiagnosticPhaseKind.Queued,
                ResponseHeadersReceived = false,
            },
            () => new OperationCanceledException("provider concurrency slot wait cancelled"),
            "Docs/08_how_debuge/06-延迟问题定位.md"),

        new("unclassified_local", DiagnosticCauseCode.UnclassifiedLocal,
            DiagnosticPhaseKind.AwaitResponseHeaders, false, null, Baseline,
            () => new InvalidOperationException("local state machine hit an unexpected branch"),
            "Docs/12_features/可诊断基础设施设计-2026-10-07.md"),

        new("unclassified_transport", DiagnosticCauseCode.UnclassifiedTransport,
            DiagnosticPhaseKind.AwaitResponseHeaders, true, null, Baseline,
            () => new HttpRequestException("transport", new IOException("garbage in the wire")),
            "Docs/12_features/可诊断基础设施设计-2026-10-07.md"),
    ];

    /// <summary>
    /// 事故复刻场景（2026-10-07）：调用方传入「写请求体」阶段提示，
    /// 分类器在默认指纹缺失时也能通过现场提示落到正确阶段与码。
    /// </summary>
    public static FaultScenario IncidentReplica { get; } = new(
        UploadResetScenarioName,
        DiagnosticCauseCode.RequestUploadReset,
        DiagnosticPhaseKind.RequestUpload,
        true,
        null,
        Baseline with { PhaseHint = DiagnosticPhaseKind.RequestUpload },
        CreateUploadResetChain,
        "Docs/08_how_debuge/06-延迟问题定位.md#79-responseended-后-agent-联系人持续显示异常");

    /// <summary>
    /// 复刻真实异常链：<c>HttpRequestException("Error while copying content to a stream.")</c>
    /// → <c>IOException("Unable to write data to the transport connection")</c> → <c>SocketException(10054)</c>。
    /// 三层分别在独立方法里抛出/包装，使 <see cref="Exception.StackTrace"/> 真实存在，
    /// 帧指纹判定才有可判之物。
    /// </summary>
    public static Exception CreateUploadResetChain()
    {
        try
        {
            try
            {
                try
                {
                    throw new SocketException(10054);
                }
                catch (SocketException socket)
                {
                    throw new IOException("Unable to write data to the transport connection: 远程主机强迫关闭了一个现有的连接。", socket);
                }
            }
            catch (IOException io)
            {
                throw new HttpRequestException("Error while copying content to a stream.", io);
            }
        }
        catch (Exception captured)
        {
            return captured;
        }
    }
}
