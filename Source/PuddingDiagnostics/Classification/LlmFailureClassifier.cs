using System.Net.Http;
using System.Text.Json;

namespace PuddingCode.Diagnostics;

/// <summary>
/// LLM 调用链的因果分类器（可诊断基础设施设计 §5.1/§5.3）。
/// <para>
/// 纯函数、BCL-only：输入异常 + 现场上下文，输出稳定码 + 阶段 + 可重试性 + 中文消息 + 有界证据。
/// 判定顺序（前者优先）：① 消费方透传码 → ② 取消类 → ③ 看门狗超时类 → ④ HTTP/传输/协议类 → ⑤ 诚实兜底。
/// </para>
/// <para>
/// **不做的事**：不吞异常、不改写异常、不重试、不读配置、不写日志。分类结果由调用方决定如何落地。
/// </para>
/// </summary>
public sealed class LlmFailureClassifier : IDiagnosticCauseClassifier
{
    public static readonly LlmFailureClassifier Default = new();

    private readonly IReadOnlyList<string> _uploadFrames;

    public LlmFailureClassifier()
        : this(ExceptionChainInspector.DefaultUploadFrames)
    {
    }

    /// <summary>
    /// 可注入「写请求体」帧指纹的构造：生产使用默认指纹，测试用自定义帧名验证判定机制本身
    /// （默认指纹是不可伪造的框架帧，无法在测试里真实复现）。
    /// </summary>
    public LlmFailureClassifier(IReadOnlyList<string> uploadFrames)
        => _uploadFrames = uploadFrames ?? throw new ArgumentNullException(nameof(uploadFrames));

    public DiagnosticCause Classify(Exception exception, DiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        var chain = ExceptionChainInspector.Flatten(exception);
        var resolved = Resolve(chain, context);

        var descriptor = DiagnosticCauseCatalog.Describe(resolved.Code);
        var baseRetryable = resolved.RetryableOverride ?? descriptor.RetryableByDefault;

        // 核心不变式（Docs/08_how_debuge/06-延迟问题定位.md §7.9）：
        // 一旦产出过任何增量，就不允许再按「传输瞬态」重试，否则会重复正文/重复工具调用。
        var retryable = baseRetryable && !context.HasYieldedDelta;

        return new DiagnosticCause
        {
            Code = resolved.Code,
            Category = DiagnosticCauseCode.CategoryOf(resolved.Code),
            Retryable = retryable,
            Phase = resolved.Phase,
            UserMessage = descriptor.UserMessage,
            RemediationHint = descriptor.RemediationHint,
            Evidence = BuildEvidence(chain, context, resolved, retryable),
            Rule = resolved.Rule,
        };
    }

    private ResolvedCause Resolve(IReadOnlyList<Exception> chain, DiagnosticContext context)
    {
        // ① 消费方最清楚（vision.* 透传、熔断、限流等待取消、本地体积闸门）
        if (!string.IsNullOrWhiteSpace(context.CauseCodeOverride))
        {
            return ResolvedCause.Of(
                context.CauseCodeOverride!,
                "override",
                context.OverridePhase ?? InferPhase(context).Phase,
                context.OverridePhase is null ? InferPhase(context).Detection : "override",
                context.OverrideRetryable);
        }

        // ② 取消类
        if (chain.Any(e => e is OperationCanceledException))
        {
            var phase = InferPhase(context);

            if (context.UserCancelled)
                return ResolvedCause.Of(DiagnosticCauseCode.Cancelled, "cancel:user", phase.Phase, phase.Detection, false);

            if (context.FirstChunkTimeout)
                return ResolvedCause.Of(DiagnosticCauseCode.FirstChunkTimeout, "cancel:first_chunk_timeout",
                    AwaitingOrReading(context), "header_state", null);

            if (context.StreamIdleTimeout)
                return ResolvedCause.Of(DiagnosticCauseCode.StreamIdleTimeout, "cancel:stream_idle_timeout",
                    DiagnosticPhaseKind.ReadResponseStream, "timeout_flag", null);

            if (context.HttpClientTimeout)
                return ResolvedCause.Of(DiagnosticCauseCode.HttpClientTimeout, "cancel:http_client_timeout",
                    phase.Phase, phase.Detection, null);

            // 没有任何超时/取消标志：如实标为「原因未知的取消」，不假装是超时。
            return ResolvedCause.Of(DiagnosticCauseCode.Cancelled, "cancel:unknown", phase.Phase, phase.Detection, false);
        }

        // ③ 看门狗超时类（DirectLlmClient 把看门狗超时包装成 TimeoutException）
        if (chain.Any(e => e is TimeoutException))
        {
            if (context.FirstChunkTimeout)
                return ResolvedCause.Of(DiagnosticCauseCode.FirstChunkTimeout, "timeout:first_chunk",
                    AwaitingOrReading(context), "timeout_flag", null);

            if (context.StreamIdleTimeout)
                return ResolvedCause.Of(DiagnosticCauseCode.StreamIdleTimeout, "timeout:stream_idle",
                    DiagnosticPhaseKind.ReadResponseStream, "timeout_flag", null);

            var inferred = context.ResponseHeadersReceived == true
                ? DiagnosticCauseCode.StreamIdleTimeout
                : DiagnosticCauseCode.FirstChunkTimeout;

            return ResolvedCause.Of(inferred, "timeout:phase_inference", AwaitingOrReading(context), "header_state", null);
        }

        // ④a HTTP 状态码（网关用 HttpRequestException(message, inner:null, status) 保留状态）
        var httpStatus = chain.OfType<HttpRequestException>()
            .Select(e => e.StatusCode)
            .FirstOrDefault(status => status is not null);

        if (httpStatus is { } status)
        {
            var numeric = (int)status;
            var (code, rule) = numeric switch
            {
                401 or 403 => (DiagnosticCauseCode.Http401Or403, "http:401_403"),
                404 => (DiagnosticCauseCode.Http404, "http:404"),
                408 => (DiagnosticCauseCode.HttpClientTimeout, "http:408"),
                413 or 422 => (DiagnosticCauseCode.RequestTooLarge, "http:413_422"),
                429 => (DiagnosticCauseCode.Http429, "http:429"),
                >= 500 => (DiagnosticCauseCode.Http5xx, "http:5xx"),
                >= 400 => (DiagnosticCauseCode.Http400, "http:4xx"),
                >= 300 => (DiagnosticCauseCode.ProtocolInvalid, "http:3xx"),
                _ => (DiagnosticCauseCode.ProtocolInvalid, "http:other"),
            };

            var phase = InferPhase(context);
            return ResolvedCause.Of(code, rule, phase.Phase, phase.Detection, null, numeric);
        }

        // ④b 无状态码的传输/协议失败
        var socketCode = ExceptionChainInspector.SocketErrorCode(chain);
        var uploadFrame = ExceptionChainInspector.FindUploadFrame(chain, _uploadFrames);
        var httpRequestError = ExceptionChainInspector.HttpRequestErrorName(chain);

        if (ExceptionChainInspector.IsDnsFailure(socketCode))
        {
            var phase = InferPhase(context);
            return ResolvedCause.Of(DiagnosticCauseCode.DnsFailed, "socket:dns", phase.Phase, phase.Detection, null, null, socketCode);
        }

        if (ExceptionChainInspector.IsConnectFailure(socketCode))
        {
            var phase = InferPhase(context);
            return ResolvedCause.Of(DiagnosticCauseCode.ConnectFailed, "socket:connect", phase.Phase, phase.Detection, null, null, socketCode);
        }

        if (chain.Any(e => e is System.Security.Authentication.AuthenticationException))
        {
            var phase = InferPhase(context);
            return ResolvedCause.Of(DiagnosticCauseCode.TlsFailed, "tls:authentication", phase.Phase, phase.Detection, null, null, socketCode);
        }

        // 写请求体的框架帧就是确凿指纹：无论内因是 RST、IOException 还是别的 IO 错误。
        if (uploadFrame is not null)
        {
            return ResolvedCause.Of(DiagnosticCauseCode.RequestUploadReset, $"stack_frame:{uploadFrame}",
                DiagnosticPhaseKind.RequestUpload, $"stack_frame:{uploadFrame}", null, null, socketCode);
        }

        if (chain.Any(e => e is HttpIOException or IOException or System.Net.Sockets.SocketException))
        {
            if (string.Equals(httpRequestError, "ResponseEnded", StringComparison.Ordinal))
            {
                return ResolvedCause.Of(DiagnosticCauseCode.ResponseReadReset, "http_io:response_ended",
                    DiagnosticPhaseKind.ReadResponseStream, "exception_type", null, null, socketCode);
            }

            if (context.ResponseHeadersReceived == true || context.PhaseHint == DiagnosticPhaseKind.ReadResponseStream)
            {
                return ResolvedCause.Of(DiagnosticCauseCode.ResponseReadReset, "transport:reset_after_headers",
                    DiagnosticPhaseKind.ReadResponseStream, "header_state", null, null, socketCode);
            }

            if (ExceptionChainInspector.IsConnectionReset(socketCode))
            {
                // 有 RST 但没有阶段指纹、也没收到响应头：如实标为未定性，阶段按现场提示。
                var phase = context.PhaseHint == DiagnosticPhaseKind.Unknown
                    ? DiagnosticPhaseKind.AwaitResponseHeaders
                    : context.PhaseHint;

                return ResolvedCause.Of(DiagnosticCauseCode.UnclassifiedTransport, "transport:reset_without_phase_signal",
                    phase, "context_hint", null, null, socketCode);
            }

            var fallbackPhase = InferPhase(context);
            return ResolvedCause.Of(DiagnosticCauseCode.UnclassifiedTransport, "transport:unrecognized",
                fallbackPhase.Phase, fallbackPhase.Detection, null, null, socketCode);
        }

        // ④c 协议解析失败（SSE / JSON）
        if (chain.Any(e => e is JsonException))
        {
            return ResolvedCause.Of(DiagnosticCauseCode.ProtocolInvalid, "protocol:json_invalid",
                DiagnosticPhaseKind.ReadResponseStream, "exception_type", null);
        }

        if (chain.Any(e => e is HttpRequestException))
        {
            // 有 HttpRequestException 但没有状态码、没有可识别内因：如实兜底，不猜具体网络原因。
            var phase = InferPhase(context);
            return ResolvedCause.Of(DiagnosticCauseCode.UnclassifiedTransport, "http:without_status_or_inner",
                phase.Phase, phase.Detection, null);
        }

        // ⑤ 诚实兜底
        var lastPhase = InferPhase(context);
        return ResolvedCause.Of(DiagnosticCauseCode.UnclassifiedLocal, "fallback:unclassified",
            lastPhase.Phase, lastPhase.Detection, false);
    }

    private static IReadOnlyDictionary<string, string> BuildEvidence(
        IReadOnlyList<Exception> chain,
        DiagnosticContext context,
        ResolvedCause resolved,
        bool retryable)
    {
        var builder = new DiagnosticEvidenceBuilder();

        builder
            .Add(DiagnosticEvidenceKeys.ProviderId, context.ProviderId)
            .Add(DiagnosticEvidenceKeys.ModelId, context.ModelId)
            .Add(DiagnosticEvidenceKeys.EndpointHost, context.EndpointHost)
            .Add(DiagnosticEvidenceKeys.Attempt, context.Attempt)
            .Add(DiagnosticEvidenceKeys.MaxRetries, context.MaxRetries)
            .Add(DiagnosticEvidenceKeys.DispatchCount, context.DispatchCount ?? 1)
            .Add(DiagnosticEvidenceKeys.Phase, DiagnosticPhases.ToWire(resolved.Phase))
            .Add(DiagnosticEvidenceKeys.PhaseDetection, resolved.PhaseDetection)
            .Add(DiagnosticEvidenceKeys.FailureAfterMs, context.FailureAfterMs)
            .Add(DiagnosticEvidenceKeys.SinceDispatchMs, context.SinceDispatchMs)
            .Add(DiagnosticEvidenceKeys.ResponseHeadersReceived, context.ResponseHeadersReceived)
            .Add(DiagnosticEvidenceKeys.RequestBytes, context.RequestBytes)
            .Add(DiagnosticEvidenceKeys.MessageCount, context.MessageCount)
            .Add(DiagnosticEvidenceKeys.ToolCount, context.ToolCount)
            .Add(DiagnosticEvidenceKeys.HasYieldedDelta, context.HasYieldedDelta)
            .Add(DiagnosticEvidenceKeys.Retryable, retryable)
            .Add(DiagnosticEvidenceKeys.Rule, resolved.Rule)
            .Add(DiagnosticEvidenceKeys.ExceptionChain, ExceptionChainInspector.Describe(chain))
            .Add(DiagnosticEvidenceKeys.SocketErrorCode, resolved.SocketErrorCode)
            .Add(DiagnosticEvidenceKeys.HttpStatus, resolved.HttpStatus)
            .AddIf(context.UserCancelled, DiagnosticEvidenceKeys.CancelReason, "user_cancelled")
            .AddIf(context.FirstChunkTimeout, "timeout_kind", "first_chunk")
            .AddIf(context.StreamIdleTimeout, "timeout_kind", "stream_idle")
            .AddIf(context.HttpClientTimeout, "timeout_kind", "http_client");

        return builder.Build();
    }

    /// <summary>收到响应头之前＝等待响应头；之后＝读响应流。</summary>
    private static DiagnosticPhaseKind AwaitingOrReading(DiagnosticContext context)
        => context.ResponseHeadersReceived == true
            ? DiagnosticPhaseKind.ReadResponseStream
            : DiagnosticPhaseKind.AwaitResponseHeaders;

    /// <summary>按现场提示推断阶段；提示缺失时用「响应头是否已到」这一可观测事实，而不是猜。</summary>
    private static (DiagnosticPhaseKind Phase, string Detection) InferPhase(DiagnosticContext context)
    {
        if (context.PhaseHint != DiagnosticPhaseKind.Unknown)
            return (context.PhaseHint, "context_hint");

        return context.ResponseHeadersReceived == true
            ? (DiagnosticPhaseKind.ReadResponseStream, "header_state")
            : (DiagnosticPhaseKind.AwaitResponseHeaders, "header_state");
    }

    private sealed record ResolvedCause(
        string Code,
        string Rule,
        DiagnosticPhaseKind Phase,
        string PhaseDetection,
        bool? RetryableOverride,
        int? HttpStatus = null,
        int? SocketErrorCode = null)
    {
        internal static ResolvedCause Of(
            string code,
            string rule,
            DiagnosticPhaseKind phase,
            string phaseDetection,
            bool? retryableOverride,
            int? httpStatus = null,
            int? socketErrorCode = null)
            => new(code, rule, phase, phaseDetection, retryableOverride, httpStatus, socketErrorCode);
    }
}
