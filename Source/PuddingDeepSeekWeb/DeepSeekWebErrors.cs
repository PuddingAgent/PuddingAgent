namespace PuddingDeepSeekWeb;

/// <summary>
/// DeepSeek Search / Fetch 的错误分类。HTTP 状态码 → 语义的映射以官方文档为准：
/// 400 请求不合法、401 鉴权失败、402 余额不足、403 Fetch 被安全策略拒绝、
/// 404 Fetch 未获取到可用正文、408 读取超时、413 请求体过大、429 限速、5xx 服务端错误。
/// </summary>
public enum DeepSeekWebErrorKind
{
    /// <summary>本地参数/配置校验失败，请求没有发出（或参数层失败）。</summary>
    InvalidRequest = 0,

    /// <summary>API Key 缺失或无效（HTTP 401）。</summary>
    Unauthorized,

    /// <summary>账号余额不足（HTTP 402）。</summary>
    InsufficientBalance,

    /// <summary>Fetch 请求被安全策略拒绝（HTTP 403）。</summary>
    Forbidden,

    /// <summary>Fetch 未能获取可用网页内容（HTTP 404）：URL 不可用、被站点拦截、无 Markdown 正文等。</summary>
    NotFound,

    /// <summary>读取请求体/下游超时（HTTP 408）。</summary>
    RequestTimeout,

    /// <summary>请求体超过大小限制（HTTP 413）。</summary>
    PayloadTooLarge,

    /// <summary>超过 Search / Fetch QPS 限速（HTTP 429）。</summary>
    RateLimited,

    /// <summary>服务端内部错误或下游超时（HTTP 5xx）。</summary>
    ServerError,

    /// <summary>2xx 但响应体不是可解析的 DeepSeek JSON。</summary>
    InvalidResponse,

    /// <summary>传输层失败（连接失败、TLS、传输实现抛异常等）。</summary>
    Transport,
}

/// <summary>一次 DeepSeek 调用的结构化错误（不含 API Key，也不含鉴权头）。</summary>
public sealed record DeepSeekWebError
{
    public required DeepSeekWebErrorKind Kind { get; init; }

    /// <summary>可直接展示给用户的错误说明（有界、已脱敏）。</summary>
    public required string Message { get; init; }

    /// <summary>HTTP 状态码；本地校验失败或传输异常时为 null。</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>OpenAI 兼容错误块的 error.type（若上游返回）。</summary>
    public string? ErrorType { get; init; }

    /// <summary>OpenAI 兼容错误块的 error.code（若上游返回）。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>是否属于官方文档允许重试的类别（408 / 429 / 5xx / 传输失败）。</summary>
    public bool IsRetryable => Kind
        is DeepSeekWebErrorKind.RequestTimeout
        or DeepSeekWebErrorKind.RateLimited
        or DeepSeekWebErrorKind.ServerError
        or DeepSeekWebErrorKind.Transport;
}

/// <summary>组件的统一返回：成功带强类型值，失败带结构化错误；用返回值而非异常表达 API 错误。</summary>
public sealed record DeepSeekWebResult<T>
    where T : class
{
    public T? Value { get; init; }

    public DeepSeekWebError? Error { get; init; }

    public bool IsSuccess => Error is null && Value is not null;

    public static DeepSeekWebResult<T> Ok(T value) => new() { Value = value };

    public static DeepSeekWebResult<T> Fail(DeepSeekWebError error) => new() { Error = error };

    public static DeepSeekWebResult<T> Fail(
        DeepSeekWebErrorKind kind,
        string message,
        int? httpStatusCode = null) => new()
    {
        Error = new DeepSeekWebError
        {
            Kind = kind,
            Message = message,
            HttpStatusCode = httpStatusCode,
        },
    };
}
