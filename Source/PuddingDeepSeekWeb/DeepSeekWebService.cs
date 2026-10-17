using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingDeepSeekWeb;

/// <summary>
/// <see cref="IDeepSeekWebService"/> 的默认实现：参数校验 → 构造线上 JSON → 经
/// <see cref="IDeepSeekWebTransport"/> 发送 → 解析成功响应 / 把 HTTP 错误映射为
/// <see cref="DeepSeekWebError"/> → 对官方文档允许的类别做有界重试。
/// <para>
/// 本类不读配置文件、不持有长期凭据、不直接使用任何 HTTP 栈；API Key 只出现在出站
/// <c>Authorization</c> 头里，永远不会进入错误消息（错误消息在返回前会再做一次脱敏）。
/// </para>
/// </summary>
public sealed class DeepSeekWebService : IDeepSeekWebService
{
    /// <summary>默认重试次数（首次尝试之外的重试次数）。</summary>
    public const int DefaultMaxRetryAttempts = 2;

    /// <summary>Search query 允许的最大 Unicode code point 数。</summary>
    public const int MaxSearchQueryRunes = 128;

    /// <summary>Search count 允许的最小值。</summary>
    public const int MinSearchCount = 1;

    /// <summary>Search count 允许的最大值。</summary>
    public const int MaxSearchCount = 50;

    /// <summary>ori_question 允许的最大 Unicode code point 数。</summary>
    public const int MaxOriQuestionRunes = 128;

    /// <summary>Fetch url 允许的最大字符数。</summary>
    public const int MaxFetchUrlLength = 8192;

    /// <summary>非 JSON 错误体写进错误消息时的正文截断长度。</summary>
    public const int MaxErrorBodyLength = 512;

    /// <summary>指数退避的基准延迟：失败后依次 1s、2s、4s…（默认实现）。</summary>
    public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>单次重试延迟上限（与 Retry-After 的合法上限一致）。</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDeepSeekWebTransport _transport;
    private readonly int _maxRetryAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    /// <summary>使用默认重试次数（<see cref="DefaultMaxRetryAttempts"/>）与真实 <see cref="Task.Delay"/>。</summary>
    public DeepSeekWebService(IDeepSeekWebTransport transport)
        : this(transport, DefaultMaxRetryAttempts, null)
    {
    }

    /// <summary>
    /// 创建服务实例。
    /// </summary>
    /// <param name="transport">出站传输端口（组件不绑定 HTTP 栈）。</param>
    /// <param name="maxRetryAttempts">首次尝试之外的重试次数；必须 ≥ 0。</param>
    /// <param name="delayAsync">
    /// 重试等待函数；null = <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。
    /// 测试可注入记录器以避免真实等待。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetryAttempts"/> 为负数。</exception>
    public DeepSeekWebService(
        IDeepSeekWebTransport transport,
        int maxRetryAttempts,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetryAttempts);

        _transport = transport;
        _maxRetryAttempts = maxRetryAttempts;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task<DeepSeekWebResult<DeepSeekSearchResponse>> SearchAsync(
        DeepSeekWebConnection connection,
        DeepSeekSearchRequest request,
        CancellationToken ct = default)
    {
        if (connection is null)
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(Invalid("DeepSeek 连接配置为空。"));
        if (request is null)
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(Invalid("Search 请求为空。"));
        if (ValidateConnection(connection) is { } connectionError)
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(connectionError);
        if (ValidateSearchRequest(request) is { } requestError)
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(requestError);

        var url = DeepSeekWebEndpoints.BuildUrl(connection.BaseUrl, DeepSeekWebEndpoints.SearchPath);
        if (url is null)
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(Invalid(BaseUrlInvalidMessage));

        var wire = new SearchWireRequest
        {
            Query = request.Query.Trim(),
            Count = request.Count,
            OriQuestion = NormalizeOptional(request.OriQuestion),
            Localization = NormalizeOptional(request.Localization),
            NeedContent = request.NeedContent,
        };

        var transportRequest = CreateTransportRequest(
            connection,
            url,
            JsonSerializer.Serialize(wire, JsonOptions));

        return await ExecuteAsync(connection.ApiKey, transportRequest, ParseSearchResponse, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeepSeekWebResult<DeepSeekFetchResponse>> FetchAsync(
        DeepSeekWebConnection connection,
        DeepSeekFetchRequest request,
        CancellationToken ct = default)
    {
        if (connection is null)
            return DeepSeekWebResult<DeepSeekFetchResponse>.Fail(Invalid("DeepSeek 连接配置为空。"));
        if (request is null)
            return DeepSeekWebResult<DeepSeekFetchResponse>.Fail(Invalid("Fetch 请求为空。"));
        if (ValidateConnection(connection) is { } connectionError)
            return DeepSeekWebResult<DeepSeekFetchResponse>.Fail(connectionError);
        if (ValidateFetchRequest(request) is { } requestError)
            return DeepSeekWebResult<DeepSeekFetchResponse>.Fail(requestError);

        var url = DeepSeekWebEndpoints.BuildUrl(connection.BaseUrl, DeepSeekWebEndpoints.FetchPath);
        if (url is null)
            return DeepSeekWebResult<DeepSeekFetchResponse>.Fail(Invalid(BaseUrlInvalidMessage));

        var wire = new FetchWireRequest { Url = request.Url.Trim() };
        var transportRequest = CreateTransportRequest(
            connection,
            url,
            JsonSerializer.Serialize(wire, JsonOptions));

        return await ExecuteAsync(connection.ApiKey, transportRequest, ParseFetchResponse, ct)
            .ConfigureAwait(false);
    }

    private const string BaseUrlInvalidMessage =
        "DeepSeek Base URL 配置不合法：必须是带主机名的 http 或 https 绝对地址。";

    private static DeepSeekWebError Invalid(string message) => new()
    {
        Kind = DeepSeekWebErrorKind.InvalidRequest,
        Message = message,
    };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------------------------------------------------------------------
    // 校验（全部在调用 transport 之前完成；失败不产生任何出站请求）
    // ---------------------------------------------------------------------

    private static DeepSeekWebError? ValidateConnection(DeepSeekWebConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            return Invalid(
                "DeepSeek API Key 未配置：请先在 LLM 服务商设置中填写 DeepSeek API Key。"
                + "（错误消息不会回显已配置的 Key。）");
        }

        if (DeepSeekWebEndpoints.NormalizeBaseUrl(connection.BaseUrl) is null)
            return Invalid(BaseUrlInvalidMessage);

        return null;
    }

    private static DeepSeekWebError? ValidateSearchRequest(DeepSeekSearchRequest request)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return Invalid("query 不能为空或全为空白。");

        var queryRunes = query.EnumerateRunes().Count();
        if (queryRunes > MaxSearchQueryRunes)
        {
            return Invalid(
                $"query 长度为 {queryRunes} 个 Unicode 字符，超过上限 {MaxSearchQueryRunes}。");
        }

        if (request.Count is { } count && (count < MinSearchCount || count > MaxSearchCount))
        {
            return Invalid(
                $"count 必须在 {MinSearchCount}–{MaxSearchCount} 之间，实际为 {count}。");
        }

        var oriQuestion = NormalizeOptional(request.OriQuestion);
        if (oriQuestion is not null && oriQuestion.EnumerateRunes().Count() > MaxOriQuestionRunes)
        {
            return Invalid(
                $"ori_question 长度超过上限 {MaxOriQuestionRunes} 个 Unicode 字符。");
        }

        var localization = NormalizeOptional(request.Localization);
        if (localization is not null && !IPAddress.TryParse(localization, out _))
            return Invalid("localization 必须是合法的 IP 地址。");

        return null;
    }

    private static DeepSeekWebError? ValidateFetchRequest(DeepSeekFetchRequest request)
    {
        var url = request.Url?.Trim() ?? string.Empty;
        if (url.Length == 0)
            return Invalid("url 不能为空或全为空白。");

        if (url.Length > MaxFetchUrlLength)
        {
            return Invalid(
                $"url 长度为 {url.Length} 个字符，超过上限 {MaxFetchUrlLength}。");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Invalid("url 必须是合法的绝对 URL。");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Invalid("url 只支持 http 或 https 协议。");

        if (string.IsNullOrEmpty(uri.Host))
            return Invalid("url 必须包含主机名（host）。");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Invalid("url 不得包含用户名或密码（userinfo）。");

        return null;
    }

    // ---------------------------------------------------------------------
    // 出站请求构造
    // ---------------------------------------------------------------------

    private static DeepSeekWebTransportRequest CreateTransportRequest(
        DeepSeekWebConnection connection,
        string url,
        string body) => new()
        {
            Url = url,
            Method = "POST",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + connection.ApiKey,
            },
            Body = body,
            ContentType = "application/json",
            TimeoutSeconds = connection.TimeoutSeconds,
        };

    // ---------------------------------------------------------------------
    // 发送 + 有界重试
    // ---------------------------------------------------------------------

    private async Task<DeepSeekWebResult<T>> ExecuteAsync<T>(
        string apiKey,
        DeepSeekWebTransportRequest request,
        Func<DeepSeekWebTransportResponse, DeepSeekWebResult<T>> parseSuccess,
        CancellationToken ct)
        where T : class
    {
        DeepSeekWebResult<T>? lastFailure = null;

        for (var attempt = 0; attempt <= _maxRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            DeepSeekWebResult<T> result;
            string? retryAfter = null;

            try
            {
                var response = await _transport.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode is >= 200 and < 300)
                {
                    result = parseSuccess(response);
                }
                else
                {
                    retryAfter = response.GetHeader("Retry-After");
                    result = DeepSeekWebResult<T>.Fail(MapHttpError(response));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方取消：必须原样传播，不能被包装成可重试的 Transport 错误。
                throw;
            }
            catch (Exception ex)
            {
                result = DeepSeekWebResult<T>.Fail(new DeepSeekWebError
                {
                    Kind = DeepSeekWebErrorKind.Transport,
                    Message = "DeepSeek 请求传输失败：" + Redact(ex.Message, apiKey),
                });
            }

            // 最后一道脱敏：任何错误消息都不允许带出 API Key。
            if (result.Error is { } error)
            {
                result = DeepSeekWebResult<T>.Fail(Redact(error, apiKey));
                if (!error.IsRetryable)
                    return result;
                lastFailure = result;
            }
            else
            {
                return result;
            }

            if (attempt == _maxRetryAttempts)
                break;

            var delay = ResolveRetryDelay(attempt, retryAfter);
            await _delayAsync(delay, ct).ConfigureAwait(false);
        }

        return lastFailure!;
    }

    /// <summary>
    /// 计算下一次重试前的等待时间：响应带合法 <c>Retry-After</c>（1–30 整数秒）时优先使用，
    /// 否则按 1s、2s、4s… 指数退避，并封顶 <see cref="MaxRetryDelay"/>。
    /// </summary>
    private static TimeSpan ResolveRetryDelay(int attempt, string? retryAfter)
    {
        if (int.TryParse(
                retryAfter?.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var retryAfterSeconds)
            && retryAfterSeconds is >= 1 and <= 30)
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        var exponent = Math.Min(attempt, 5);
        var delay = TimeSpan.FromSeconds(BaseRetryDelay.TotalSeconds * Math.Pow(2, exponent));
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    // ---------------------------------------------------------------------
    // 响应解析
    // ---------------------------------------------------------------------

    private static DeepSeekWebResult<DeepSeekSearchResponse> ParseSearchResponse(
        DeepSeekWebTransportResponse response)
    {
        if (!TryParseJsonObject(response.Body, out var root))
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(InvalidResponse(
                "DeepSeek Search 响应体不是可解析的 JSON 对象。"));

        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind == JsonValueKind.Null)
        {
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(InvalidResponse(
                "DeepSeek Search 响应缺少 results 字段或为 null。"));
        }

        if (results.ValueKind != JsonValueKind.Array)
        {
            return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(InvalidResponse(
                "DeepSeek Search 响应的 results 不是数组。"));
        }

        var parsed = new List<DeepSeekSearchResult>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return DeepSeekWebResult<DeepSeekSearchResponse>.Fail(InvalidResponse(
                    "DeepSeek Search 响应的 results 元素不是 JSON 对象。"));
            }

            parsed.Add(new DeepSeekSearchResult
            {
                RerankScore = ReadDouble(item, "rerank_score") ?? 0,
                Title = ReadString(item, "title") ?? string.Empty,
                Url = ReadString(item, "url") ?? string.Empty,
                Snippet = ReadString(item, "snippet") ?? string.Empty,
                Summary = ReadString(item, "summary") ?? string.Empty,
                Content = ReadString(item, "content") ?? string.Empty,
                SiteName = ReadString(item, "site_name"),
                SiteIcon = ReadString(item, "site_icon"),
                Date = ReadString(item, "date"),
                Authority = ReadInt32(item, "authority") ?? -1,
            });
        }

        return DeepSeekWebResult<DeepSeekSearchResponse>.Ok(
            new DeepSeekSearchResponse { Results = parsed });
    }

    private static DeepSeekWebResult<DeepSeekFetchResponse> ParseFetchResponse(
        DeepSeekWebTransportResponse response)
    {
        if (!TryParseJsonObject(response.Body, out var root))
            return DeepSeekWebResult<DeepSeekFetchResponse>.Fail(InvalidResponse(
                "DeepSeek Fetch 响应体不是可解析的 JSON 对象。"));

        return DeepSeekWebResult<DeepSeekFetchResponse>.Ok(new DeepSeekFetchResponse
        {
            Title = ReadString(root, "title") ?? string.Empty,
            Content = ReadString(root, "content") ?? string.Empty,
        });
    }

    private static DeepSeekWebError InvalidResponse(string message) => new()
    {
        Kind = DeepSeekWebErrorKind.InvalidResponse,
        Message = message,
    };

    private static bool TryParseJsonObject(string? body, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    // ---------------------------------------------------------------------
    // HTTP 错误映射（OpenAI 兼容错误块优先）
    // ---------------------------------------------------------------------

    private static DeepSeekWebError MapHttpError(DeepSeekWebTransportResponse response)
    {
        var kind = response.StatusCode switch
        {
            400 => DeepSeekWebErrorKind.InvalidRequest,
            401 => DeepSeekWebErrorKind.Unauthorized,
            402 => DeepSeekWebErrorKind.InsufficientBalance,
            403 => DeepSeekWebErrorKind.Forbidden,
            404 => DeepSeekWebErrorKind.NotFound,
            408 => DeepSeekWebErrorKind.RequestTimeout,
            413 => DeepSeekWebErrorKind.PayloadTooLarge,
            429 => DeepSeekWebErrorKind.RateLimited,
            >= 500 and < 600 => DeepSeekWebErrorKind.ServerError,
            _ => DeepSeekWebErrorKind.InvalidRequest,
        };

        var (upstreamMessage, errorType, errorCode) = TryReadErrorBlock(response.Body);
        var message = string.IsNullOrWhiteSpace(upstreamMessage)
            ? BuildStatusMessage(response)
            : $"DeepSeek API 错误（HTTP {response.StatusCode}）：{upstreamMessage.Trim()}";

        return new DeepSeekWebError
        {
            Kind = kind,
            Message = message,
            HttpStatusCode = response.StatusCode,
            ErrorType = errorType,
            ErrorCode = errorCode,
        };
    }

    /// <summary>解析 OpenAI 兼容错误块 <c>{"error":{"message","type","param","code"}}</c>。</summary>
    private static (string? Message, string? Type, string? Code) TryReadErrorBlock(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            return (
                ReadString(error, "message"),
                ReadScalar(error, "type"),
                ReadScalar(error, "code"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? ReadScalar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string BuildStatusMessage(DeepSeekWebTransportResponse response)
    {
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? string.Empty
            : " " + response.ReasonPhrase.Trim();
        var body = Truncate(response.Body, MaxErrorBodyLength);

        var message = $"DeepSeek 请求失败：HTTP {response.StatusCode}{reason}";
        return string.IsNullOrWhiteSpace(body) ? message : message + "：" + body;
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…（已截断）";
    }

    // ---------------------------------------------------------------------
    // 脱敏
    // ---------------------------------------------------------------------

    private static DeepSeekWebError Redact(DeepSeekWebError error, string apiKey) =>
        string.IsNullOrEmpty(apiKey) || !error.Message.Contains(apiKey, StringComparison.Ordinal)
            ? error
            : error with { Message = error.Message.Replace(apiKey, "***", StringComparison.Ordinal) };

    private static string Redact(string? message, string apiKey)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        return string.IsNullOrEmpty(apiKey)
            ? message
            : message.Replace(apiKey, "***", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // 线上 JSON 形状（只用于出站序列化；响应解析用 JsonDocument 以容忍未知字段）
    // ---------------------------------------------------------------------

    private sealed class SearchWireRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; init; } = string.Empty;

        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Count { get; init; }

        [JsonPropertyName("ori_question")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OriQuestion { get; init; }

        [JsonPropertyName("localization")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Localization { get; init; }

        /// <summary>false 也显式输出（官方默认 false），让线上请求自描述、便于排障。</summary>
        [JsonPropertyName("need_content")]
        public bool NeedContent { get; init; }
    }

    private sealed class FetchWireRequest
    {
        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;
    }
}
