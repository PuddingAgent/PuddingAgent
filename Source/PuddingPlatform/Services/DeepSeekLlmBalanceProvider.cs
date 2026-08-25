using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// DeepSeek 余额适配器：GET {baseUrl}/user/balance（Bearer 认证）。
/// 余额端点挂在站点根，baseUrl 尾部 /v1 会被剥掉（https://api.deepseek.com/user/balance）。
/// 响应格式：{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00",...}]}
/// （金额为字符串数字）。
/// </summary>
public sealed class DeepSeekLlmBalanceProvider : ILlmBalanceProvider
{
    /// <summary>Named HttpClient used for live balance queries（30s 超时，见 DI 注册）。</summary>
    public const string BalanceHttpClientName = "LlmBalanceQuery";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<DeepSeekLlmBalanceProvider>? _logger;

    public DeepSeekLlmBalanceProvider(
        IHttpClientFactory? httpClientFactory = null,
        ILogger<DeepSeekLlmBalanceProvider>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>ProviderId 含 "deepseek" 或 BaseUrl 指向 deepseek.com（含自建反代命名）。</summary>
    public bool CanHandle(PuddingLlmProviderConfig provider) =>
        provider.ProviderId.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
        || provider.BaseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase);

    public async Task<LlmProviderBalanceDto> QueryAsync(
        PuddingLlmProviderConfig provider,
        string apiKey,
        CancellationToken ct = default)
    {
        var url = BuildBalanceUrl(provider.BaseUrl);
        var providerId = provider.ProviderId;

        var httpClient = _httpClientFactory?.CreateClient(BalanceHttpClientName) ?? new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = TryParseBalanceErrorMessage(body)
                    ?? $"余额 API 返回状态码 {(int)response.StatusCode}";
                _logger?.LogWarning(
                    "[LlmBalance] FAIL provider={ProviderId} status={Status} elapsed={ElapsedMs}ms",
                    providerId, (int)response.StatusCode, sw.ElapsedMilliseconds);
                return new LlmProviderBalanceDto(
                    providerId, url,
                    IsAvailable: false, [], Error: errorMessage, QueriedAt: DateTimeOffset.UtcNow);
            }

            var parsed = ParseBalanceResponse(body);
            _logger?.LogInformation(
                "[LlmBalance] OK provider={ProviderId} available={IsAvailable} infos={Count} elapsed={ElapsedMs}ms",
                providerId, parsed.IsAvailable, parsed.BalanceInfos.Count, sw.ElapsedMilliseconds);
            return new LlmProviderBalanceDto(
                providerId, url,
                parsed.IsAvailable, parsed.BalanceInfos,
                Error: null, QueriedAt: DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(
                ex, "[LlmBalance] HTTP ERROR provider={ProviderId} url={Url} elapsed={ElapsedMs}ms",
                providerId, url, sw.ElapsedMilliseconds);
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger?.LogError(
                ex, "[LlmBalance] TIMEOUT provider={ProviderId} url={Url} elapsed={ElapsedMs}ms",
                providerId, url, sw.ElapsedMilliseconds);
            throw new HttpRequestException("余额查询请求超时", ex);
        }
    }

    /// <summary>余额端点挂在站点根：剥掉尾部 /v1 后追加 /user/balance（已带则原样）。</summary>
    internal static string BuildBalanceUrl(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            url = url[..^3];
        return url.EndsWith("/user/balance", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/user/balance";
    }

    // ── DeepSeek 余额响应 JSON 解析 ──

    private static (bool IsAvailable, List<LlmBalanceInfoDto> BalanceInfos) ParseBalanceResponse(string json)
    {
        var balanceInfos = new List<LlmBalanceInfoDto>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var isAvailable = root.TryGetProperty("is_available", out var avail)
                              && avail.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("balance_infos", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    balanceInfos.Add(new LlmBalanceInfoDto(
                        Currency: item.TryGetProperty("currency", out var c)
                            ? c.GetString() ?? "UNKNOWN"
                            : "UNKNOWN",
                        TotalBalance: TryGetDecimal(item, "total_balance"),
                        GrantedBalance: TryGetDecimal(item, "granted_balance"),
                        ToppedUpBalance: TryGetDecimal(item, "topped_up_balance")));
                }
            }

            return (isAvailable, balanceInfos);
        }
        catch (JsonException)
        {
            return (false, balanceInfos);
        }
    }

    private static decimal TryGetDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var element))
            return 0m;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var dec))
            return dec;

        // DeepSeek 实际返回字符串数字（如 "110.00"）——回退到字符串解析
        var str = element.GetString();
        return string.IsNullOrEmpty(str) || !decimal.TryParse(str, out var parsed)
            ? 0m
            : parsed;
    }

    /// <summary>
    /// 解析失败响应：{"error":{"message":"...","type":"authentication_error","param":null,"code":"invalid_request_error"}}。
    /// </summary>
    private static string? TryParseBalanceErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch (JsonException)
        {
            // 忽略非 JSON 错误响应，由调用方回退到状态码消息
        }
        return null;
    }
}
