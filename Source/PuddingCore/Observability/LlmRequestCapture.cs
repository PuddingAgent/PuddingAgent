using System.Net;
using Microsoft.Extensions.Logging;

namespace PuddingCode.Observability;

/// <summary>可选的出站请求诊断；调用身份与估算值只在进程内传递，不进入 provider 载荷。</summary>
public sealed class LlmRequestCapture : IDisposable
{
    public static readonly bool Enabled = !string.IsNullOrEmpty(
        Environment.GetEnvironmentVariable("PUDDING_CAPTURE_REQUEST"));

    private const string MessageTemplate =
        "[LlmRequestCapture] invocationId={invocationId} modelId={modelId} bodyBytes={bodyBytes} bodySha256={bodySha256} localEstimatedTokens={localEstimatedTokens}";
    private static readonly AsyncLocal<LlmRequestCapture?> Ambient = new();
    private readonly LlmRequestCapture? _previous;
    private readonly string _invocationId;
    private readonly int? _localEstimatedTokens;
    private readonly ILogger? _logger;

    public static LlmRequestCapture? Current => Ambient.Value;
    public string InvocationId => _invocationId;
    public int? LocalEstimatedTokens => _localEstimatedTokens;

    public LlmRequestCapture(string invocationId, int? localEstimatedTokens, ILogger? logger = null)
    {
        _previous = Ambient.Value;
        _invocationId = invocationId;
        _localEstimatedTokens = localEstimatedTokens;
        _logger = logger;
        Ambient.Value = this;
    }

    /// <summary>从即将发送的内容取字节并复用；保留原内容头和已知/未知长度的传输语义。</summary>
    public static async Task WriteAsync(HttpRequestMessage request, string modelId, CancellationToken ct)
    {
        var original = request.Content!;
        var length = original.Headers.ContentLength;
        var headers = original.Headers.Select(header =>
            new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray();
        var bytes = await original.ReadAsByteArrayAsync(ct);
        var fingerprint = LlmRequestBodyFingerprint.Compute(bytes);
        var content = new CapturedContent(bytes, length);
        foreach (var header in headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Content = content;
        original.Dispose();

        var capture = Current;
        var invocationId = capture?._invocationId ?? Guid.NewGuid().ToString("N");
        // 未运行估算器的独立调用用 null 表示缺失，不能伪造 0 或借用其他请求的估算。
        if (capture?._logger is { } logger)
            logger.LogInformation(MessageTemplate, invocationId, modelId,
                fingerprint.BodyBytes, fingerprint.BodySha256, capture._localEstimatedTokens);
        else
            Serilog.Log.Information(MessageTemplate, invocationId, modelId,
                fingerprint.BodyBytes, fingerprint.BodySha256, capture?._localEstimatedTokens);
    }

    public void Dispose() => Ambient.Value = _previous;

    private sealed class CapturedContent(byte[] bytes, long? originalLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            => stream.WriteAsync(bytes, ct).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = originalLength ?? 0;
            return originalLength.HasValue;
        }
    }
}
