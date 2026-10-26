using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PuddingFinance.Collection;

/// <summary>
/// 一次官方源抓取请求。
/// <para>
/// <see cref="MaxBytes"/> 是有界读取上限：官方接口一个响应可能是几 MB，
/// 全量进内存/进上下文没有意义，超出即截断并**如实置位** <c>Truncated</c>。
/// </para>
/// </summary>
public sealed record SourceFetchRequest(
    string Url,
    string Method = "GET",
    string? Body = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    int MaxBytes = OfficialSourceFetcher.DefaultMaxBytes,
    int MaxAttempts = OfficialSourceFetcher.DefaultMaxAttempts);

/// <summary>
/// 抓取结果。刻意把"证据三件套"直接带出来：字节数、SHA-256、耗时，
/// 让上层可以落盘比对，而不是只留一段无法复核的文本。
/// </summary>
public sealed record SourceFetchResult(
    string Url,
    int StatusCode,
    string? ContentType,
    string Body,
    string Sha256,
    int Bytes,
    bool Truncated,
    int Attempts,
    long ElapsedMs,
    IReadOnlyList<int> RetriedStatusCodes);

/// <summary>
/// 二进制抓取结果。PDF / 图片必须走 <see cref="OfficialSourceFetcher.FetchBytesAsync"/>：
/// 先当文本解码再回编码会把中文 PDF 变成替换字符，字节数也会对不上。
/// </summary>
public sealed record SourceBinaryResult(
    string Url,
    int StatusCode,
    string? ContentType,
    byte[] Bytes,
    string Sha256,
    bool Truncated,
    int Attempts,
    long ElapsedMs,
    IReadOnlyList<int> RetriedStatusCodes);

/// <summary>
/// 抓取失败。<see cref="IsRetryable"/> 区分"值得退避重试"和"再试也没用"：
/// 域名不在白名单是确定态，而 429/5xx 是临时态。
/// </summary>
public sealed class SourceFetchException(
    string message,
    bool isRetryable,
    int? statusCode = null,
    Exception? inner = null) : Exception(message, inner)
{
    public bool IsRetryable { get; } = isRetryable;

    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// 官方源抓取客户端（P0/P1 通道）。
///
/// 存在的理由：<c>deepseek_fetch</c> 只能取静态 HTML，而官方数据多半是
/// POST + 自定义 Header 的接口（统计局 easyquery、巨潮公告查询）。缺这一层，
/// 研究就只能停在"看价格"，进不到年报与行业景气。
///
/// 四道边界，任何一条失败都 fail-closed：
/// ① 域名白名单（<see cref="OfficialSourceHosts"/>）；② 仅 http/https 且 URL 不含凭据；
/// ③ 响应按 <see cref="SourceFetchRequest.MaxBytes"/> 有界读取；④ 重试次数有上限。
/// </summary>
public sealed class OfficialSourceFetcher
{
    public const int DefaultMaxBytes = 2 * 1024 * 1024;

    public const int DefaultMaxAttempts = 3;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan BackoffUnit = TimeSpan.FromMilliseconds(200);

    private static readonly HashSet<int> RetryableStatusCodes =
    [
        (int)HttpStatusCode.RequestTimeout,        // 408
        425,                                       // Too Early
        (int)HttpStatusCode.TooManyRequests,       // 429
        (int)HttpStatusCode.InternalServerError,   // 500
        (int)HttpStatusCode.BadGateway,            // 502
        (int)HttpStatusCode.ServiceUnavailable,    // 503
        (int)HttpStatusCode.GatewayTimeout,        // 504
    ];

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;

    /// <param name="http">外部注入的 <see cref="HttpClient"/>：单测用假 handler，不发真实请求。</param>
    /// <param name="delay">退避实现可替换，让"重试前真的等过"这件事可断言，而不用真睡 200ms。</param>
    /// <param name="timeout">单次尝试的超时。</param>
    public OfficialSourceFetcher(
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>白名单快照，供调用方在抓取前自查。</summary>
    public static IReadOnlyList<string> AllowedHosts => OfficialSourceHosts.All;

    /// <summary>取文本。二进制资源请用 <see cref="FetchBytesAsync"/>：解码会破坏非文本字节。</summary>
    public async Task<SourceFetchResult> FetchAsync(
        SourceFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        var raw = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return new SourceFetchResult(
            Url: raw.Uri.ToString(),
            StatusCode: raw.StatusCode,
            ContentType: raw.ContentType,
            Body: Decode(raw.Bytes, raw.Charset),
            Sha256: Sha256(raw.Bytes),
            Bytes: raw.Bytes.Length,
            Truncated: raw.Truncated,
            Attempts: raw.Attempts,
            ElapsedMs: raw.ElapsedMs,
            RetriedStatusCodes: raw.Retried);
    }

    /// <summary>
    /// 取原始字节。二进制资源必须走这里——先解码成 string 再回编码，
    /// 中文 PDF 会变成替换字符，而且字节数会变，后面的哈希与解析全部失真。
    /// </summary>
    public async Task<SourceBinaryResult> FetchBytesAsync(
        SourceFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        var raw = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return new SourceBinaryResult(
            Url: raw.Uri.ToString(),
            StatusCode: raw.StatusCode,
            ContentType: raw.ContentType,
            Bytes: raw.Bytes,
            Sha256: Sha256(raw.Bytes),
            Truncated: raw.Truncated,
            Attempts: raw.Attempts,
            ElapsedMs: raw.ElapsedMs,
            RetriedStatusCodes: raw.Retried);
    }

    private async Task<RawFetch> ExecuteAsync(
        SourceFetchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = Validate(request.Url);
        if (request.MaxBytes <= 0)
        {
            throw new SourceFetchException("MaxBytes 必须为正数", isRetryable: false);
        }

        var maxAttempts = Math.Max(1, request.MaxAttempts);
        var retried = new List<int>(maxAttempts);
        var stopwatch = Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            using var message = BuildRequest(uri, request);
            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_timeout);
                response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < maxAttempts)
                {
                    await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new SourceFetchException(
                    $"抓取超时（{_timeout.TotalSeconds:F0}s × {attempt} 次）：{uri}",
                    isRetryable: true);
            }
            catch (HttpRequestException exception)
            {
                if (attempt < maxAttempts)
                {
                    await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new SourceFetchException(
                    $"抓取网络失败：{uri} — {exception.Message}",
                    isRetryable: true,
                    inner: exception);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (RetryableStatusCodes.Contains(status) && attempt < maxAttempts)
                {
                    retried.Add(status);
                    await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (status >= 400)
                {
                    stopwatch.Stop();
                    throw new SourceFetchException(
                        $"HTTP {status}：{uri}（已尝试 {attempt} 次）",
                        isRetryable: RetryableStatusCodes.Contains(status),
                        statusCode: status);
                }

                var (bytes, truncated) = await ReadBoundedAsync(response, request.MaxBytes, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                return new RawFetch(
                    uri,
                    status,
                    response.Content.Headers.ContentType?.ToString(),
                    response.Content.Headers.ContentType?.CharSet,
                    bytes,
                    truncated,
                    attempt,
                    stopwatch.ElapsedMilliseconds,
                    retried);
            }
        }
    }

    /// <summary>
    /// 校验并归一化 URL。这里是 SSRF 边界本身：
    /// 协议、凭据、主机三段都必须过，缺一段就 fail-closed。
    /// </summary>
    internal static Uri Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new SourceFetchException($"URL 不是绝对地址：{url}", isRetryable: false);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SourceFetchException($"只允许 http/https，实际是 {uri.Scheme}", isRetryable: false);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SourceFetchException("URL 不得内嵌凭据", isRetryable: false);
        }

        if (!OfficialSourceHosts.IsAllowed(uri.Host))
        {
            throw new SourceFetchException(
                $"主机不在官方源白名单：{uri.Host}（允许：{string.Join(", ", OfficialSourceHosts.All)}）",
                isRetryable: false);
        }

        return uri;
    }

    /// <summary>退避：200ms、400ms、800ms…（有上限的指数增长）。</summary>
    internal static TimeSpan BackoffFor(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromMilliseconds(BackoffUnit.TotalMilliseconds * factor);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>一次成功响应的原始形态：文本与二进制两条出口共用它。</summary>
    private sealed record RawFetch(
        Uri Uri,
        int StatusCode,
        string? ContentType,
        string? Charset,
        byte[] Bytes,
        bool Truncated,
        int Attempts,
        long ElapsedMs,
        List<int> Retried);

    private static HttpRequestMessage BuildRequest(Uri uri, SourceFetchRequest request)
    {
        var method = new HttpMethod((request.Method ?? "GET").Trim().ToUpperInvariant());
        var message = new HttpRequestMessage(method, uri);
        message.Headers.TryAddWithoutValidation("User-Agent", "PuddingResearch/1.0 (+official-source)");
        message.Headers.TryAddWithoutValidation("Accept", "*/*");

        if (request.Headers is { Count: > 0 })
        {
            foreach (var (name, value) in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (request.Body is not null)
        {
            message.Content = new StringContent(
                request.Body,
                Encoding.UTF8,
                string.IsNullOrWhiteSpace(request.ContentType)
                    ? "application/x-www-form-urlencoded"
                    : request.ContentType);
        }

        return message;
    }

    /// <summary>
    /// 有界读取：最多读 <paramref name="maxBytes"/> + 1 字节。
    /// 多读的那 1 字节只用来判断"是否真的还有内容"，因此 <c>Truncated</c> 不是猜的。
    /// </summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var limit = (long)maxBytes + 1;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (buffer.Length < limit)
        {
            var room = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, room), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        var truncated = buffer.Length > maxBytes;
        var bytes = buffer.ToArray();
        if (truncated)
        {
            Array.Resize(ref bytes, maxBytes);
        }

        return (bytes, truncated);
    }

    /// <summary>
    /// 按响应声明的字符集解码；未声明则按 UTF-8。
    /// 未知字符集不做猜测——记录在 ContentType 里由调用方判断，避免"看起来正常"的乱码。
    /// </summary>
    internal static string Decode(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var name = charset?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                return Encoding.GetEncoding(name).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // 平台不支持的字符集：退回 UTF-8，调用方可以从 ContentType 看出原始声明。
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
