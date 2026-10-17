namespace PuddingDeepSeekWeb;

/// <summary>
/// 出站传输端口：组件只描述「要发什么 / 收到什么」，不绑定任何 HTTP 栈。
/// 宿主适配器（PuddingRuntime）用既有 <c>IWebClient</c> 实现它；组件测试用假传输实现它，
/// 因此本组件可以在没有网络、没有宿主进程的情况下构建与测试。
/// </summary>
public interface IDeepSeekWebTransport
{
    Task<DeepSeekWebTransportResponse> SendAsync(
        DeepSeekWebTransportRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// 一次 DeepSeek HTTP 请求的传输层描述。URL 与鉴权头属于协议知识，由组件构造；
/// 传输实现只负责把它们送达并原样带回响应。
/// </summary>
public sealed record DeepSeekWebTransportRequest
{
    public required string Url { get; init; }

    public string Method { get; init; } = "POST";

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public required string Body { get; init; }

    public string ContentType { get; init; } = "application/json";

    /// <summary>单次请求超时秒数；null = 传输默认。</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>传输层带回的原始 HTTP 响应；解析与错误分类属于组件职责，不属于传输。</summary>
public sealed record DeepSeekWebTransportResponse
{
    public required int StatusCode { get; init; }

    public string ReasonPhrase { get; init; } = string.Empty;

    public required string Body { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取指定响应头；不存在或为空时返回 null。</summary>
    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
