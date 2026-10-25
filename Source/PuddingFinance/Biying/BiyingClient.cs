using System.Net;
using System.Text;

namespace PuddingFinance.Biying;

/// <summary>调用结果。回执里带配额余量，让模型和人都能看到"今天还能查几次"。</summary>
public sealed record BiyingCallResult(
    string EndpointId,
    string Body,
    bool FromCache,
    int RemainingQuota,
    bool QuotaWarning,
    string RedactedUrl);

/// <summary>
/// 调用失败。<see cref="IsRetryable"/> 区分"可以再试一次"和"再试也没用"：
/// 证书错误重试一万次也还是错，而 5xx/网络抖动值得退避重试。
/// </summary>
public sealed class BiyingCallException(
    string message,
    bool isRetryable,
    int? providerCode = null,
    Exception? inner = null) : Exception(message, inner)
{
    public bool IsRetryable { get; } = isRetryable;

    public int? ProviderCode { get; } = providerCode;
}

/// <summary>
/// 必盈 HTTP 客户端：缓存 → 配额 → 请求 → 错误分类。
///
/// 顺序不可颠倒：**先查缓存，再扣配额**。缓存命中不该消耗配额，
/// 否则 10,000/天会在无意义的重复查询上被吃掉。
/// </summary>
public sealed class BiyingClient
{
    private readonly HttpClient _http;
    private readonly BiyingEndpointCatalog _catalog;
    private readonly BiyingOptions _options;
    private readonly BiyingResponseCache _cache;
    private readonly BiyingQuota _quota;
    private readonly IBiyingClock _clock;

    public BiyingClient(
        HttpClient httpClient,
        BiyingEndpointCatalog catalog,
        BiyingOptions options,
        IBiyingClock? clock = null,
        BiyingResponseCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Licence);

        _http = httpClient;
        _catalog = catalog;
        _options = options;
        _clock = clock ?? new SystemBiyingClock();
        _cache = cache ?? new BiyingResponseCache(clock: _clock);
        _quota = new BiyingQuota(options.DailyQuota, options.QuotaWarnRatio, _clock);
    }

    public BiyingQuota Quota => _quota;

    /// <summary>端点注册表。工具层需要它来取字段词典与元信息。</summary>
    public BiyingEndpointCatalog Catalog => _catalog;

    public async Task<BiyingCallResult> CallAsync(
        string endpointId,
        IReadOnlyList<string>? pathArgs = null,
        IReadOnlyDictionary<string, string>? query = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _catalog.Get(endpointId);
        return await CallAsync(endpoint, pathArgs, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BiyingCallResult> CallAsync(
        BiyingEndpoint endpoint,
        IReadOnlyList<string>? pathArgs = null,
        IReadOnlyDictionary<string, string>? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // URL 拼装失败属于调用方错误，**不消耗配额**：这里抛的异常早于 TryConsume。
        var url = BiyingUrlBuilder.BuildUrl(endpoint, _options.Licence, pathArgs, query);
        var cacheKey = BiyingUrlBuilder.Redact(url, _options.Licence);

        if (_cache.TryGet(cacheKey, out var cached))
        {
            return new BiyingCallResult(endpoint.Id, cached, FromCache: true,
                _quota.Remaining, _quota.IsWarning, cacheKey);
        }

        if (!_quota.TryConsume(out var remaining))
        {
            throw new BiyingCallException(
                $"必盈今日配额已用尽（{_quota.DailyLimit} 次/天），本次请求未发出。" +
                "缓存命中的查询仍可用；如需更多额度请升级证书或等 UTC 次日重置。",
                isRetryable: false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "PuddingAgent/1.0 (+finance)");

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BiyingCallException(
                $"必盈请求超时（{_options.Timeout.TotalSeconds:F0}s）：{cacheKey}", isRetryable: true);
        }
        catch (HttpRequestException exception)
        {
            throw new BiyingCallException(
                $"必盈请求网络失败：{cacheKey} — {exception.Message}", isRetryable: true, inner: exception);
        }

        using (response)
        {
            var body = await ReadBoundedAsync(response, cacheKey, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Classify(response.StatusCode, body, cacheKey);
            }

            // 必盈在 HTTP 200 时也可能返回业务错误体（例如 {"code":404,...}），必须按内容再判一次
            if (TryReadProviderError(body, out var providerCode, out var providerMessage))
            {
                throw new BiyingCallException(
                    $"必盈返回业务错误 {providerCode}：{providerMessage}（{cacheKey}）",
                    isRetryable: providerCode >= 500 || providerCode == 429,
                    providerCode: providerCode);
            }

            _cache.Set(cacheKey, body, ResolveTtl(endpoint, _options));
            return new BiyingCallResult(endpoint.Id, body, FromCache: false,
                remaining, _quota.IsWarning, cacheKey);
        }
    }

    /// <summary>
    /// 按端点类型决定缓存时长。实时行情只缓存几秒，日频缓存一小时，静态表缓存一天。
    /// 这个分类宁可粗糙也不能缺失——没有 TTL 就等于每次调用都吃配额。
    /// </summary>
    internal static TimeSpan ResolveTtl(BiyingEndpoint endpoint, BiyingOptions options)
    {
        var path = endpoint.Path;
        if (path.StartsWith("hsrl/ssjy", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/latest", StringComparison.OrdinalIgnoreCase)
            || path.Contains("real", StringComparison.OrdinalIgnoreCase))
            return options.RealtimeCacheTtl;

        if (path.Contains("history", StringComparison.OrdinalIgnoreCase)
            || path.Contains("kline", StringComparison.OrdinalIgnoreCase))
            return options.DailyCacheTtl;

        return options.StaticCacheTtl;
    }

    private async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, string cacheKey, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _options.MaxResponseBytes)
            {
                throw new BiyingCallException(
                    $"必盈响应超过本地上限 {_options.MaxResponseBytes} 字节（{cacheKey}）。" +
                    "该端点可能返回全市场数据，请改用带查询条件或代码的端点。",
                    isRetryable: false);
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static BiyingCallException Classify(HttpStatusCode status, string body, string cacheKey)
    {
        var code = (int)status;
        // 证书错误单独识别：它看起来像 404，但重试/换端点都不会好，必须让调用方改配置而不是改参数
        if (body.Contains("Licence证书不存在", StringComparison.Ordinal)
            || body.Contains("licence", StringComparison.OrdinalIgnoreCase) && code == 404)
        {
            return new BiyingCallException(
                $"必盈证书无效或被拒绝（HTTP {code}）：{cacheKey}。请检查配置里的 licence。",
                isRetryable: false, providerCode: code);
        }

        return status switch
        {
            HttpStatusCode.TooManyRequests => new BiyingCallException(
                $"必盈限流（HTTP 429）：{cacheKey}，应等待后重试。", isRetryable: true, providerCode: code),
            _ when code >= 500 => new BiyingCallException(
                $"必盈服务端错误（HTTP {code}）：{cacheKey}，可退避重试。", isRetryable: true, providerCode: code),
            _ => new BiyingCallException(
                $"必盈请求失败（HTTP {code}）：{cacheKey} — {Truncate(body, 200)}",
                isRetryable: false, providerCode: code),
        };
    }

    /// <summary>
    /// 识别业务错误体。实测必盈成功时返回数组或对象，失败时返回
    /// <c>{"code":404,"message":"...","reason":"..."}</c>——HTTP 仍是 200，靠状态码看不出来。
    /// </summary>
    internal static bool TryReadProviderError(string body, out int code, out string message)
    {
        code = 0;
        message = string.Empty;
        var trimmed = body.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out code))
                return false;
            message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;
            return code != 0 && code != 200;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
