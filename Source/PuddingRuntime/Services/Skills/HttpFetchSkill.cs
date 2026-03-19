using System.Net;
using System.Text;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// HTTP Fetch Skill——发起 HTTP GET/POST 请求并返回响应体。
/// 不需要沙箱容器，由 Runtime 宿主直接使用 HttpClient 发起请求。
/// 仅支持 http:// 和 https:// 协议，防止 SSRF 滥用内部地址。
/// </summary>
public sealed class HttpFetchSkill : IAgentSkill
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpFetchSkill> _logger;

    // 响应体最大截断长度（字符）
    private const int MaxResponseChars = 8192;

    public HttpFetchSkill(IHttpClientFactory httpClientFactory, ILogger<HttpFetchSkill> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    public string SkillId => "http_fetch";
    public string Name => "http_fetch";
    public string Description =>
        "Make an HTTP GET or POST request to a URL and return the response body. " +
        "Parameters: 'url' (required), 'method' (GET/POST, default GET), " +
        "'body' (request body for POST, optional), 'content_type' (default application/json).";
    public bool RequiresShellExecution => false;

    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var url = request.Input.Trim();
        if (string.IsNullOrEmpty(url))
            return Fail("URL is required.");

        // Security: only http/https — block SSRF to internal metadata endpoints etc.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Fail("Only http:// and https:// URLs are supported.");

        var method      = request.Parameters.TryGetValue("method", out var m) ? m.ToUpperInvariant() : "GET";
        var body        = request.Parameters.TryGetValue("body", out var b) ? b : null;
        var contentType = request.Parameters.TryGetValue("content_type", out var ct2) ? ct2 : "application/json";

        _logger.LogInformation("[HttpFetchSkill] agent={Agent} {Method} {Url}",
            request.AgentInstanceId, method, url);

        try
        {
            var client = _httpClientFactory.CreateClient("HttpFetchSkill");

            HttpResponseMessage response;
            if (method == "POST" && body is not null)
            {
                var content = new StringContent(body, Encoding.UTF8, contentType);
                response = await client.PostAsync(url, content, ct);
            }
            else
            {
                response = await client.GetAsync(url, ct);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (responseBody.Length > MaxResponseChars)
                responseBody = responseBody[..MaxResponseChars] + "\n... [truncated]";

            var statusLine = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
            return new SkillResult
            {
                Success  = response.IsSuccessStatusCode,
                Output   = $"{statusLine}\n\n{responseBody}",
                ExitCode = (int)response.StatusCode,
                Error    = response.IsSuccessStatusCode ? null : statusLine,
            };
        }
        catch (TaskCanceledException)
        {
            return Fail("Request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HttpFetchSkill] Request failed for agent={Agent} url={Url}",
                request.AgentInstanceId, url);
            return Fail(ex.Message);
        }
    }

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };
}
