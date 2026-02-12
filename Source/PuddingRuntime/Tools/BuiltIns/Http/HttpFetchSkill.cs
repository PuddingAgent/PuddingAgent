using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// HTTP Fetch Skill——发起 HTTP 请求并返回适合 Agent 上下文处理的响应内容。
/// 不需要沙箱容器，由 Runtime 宿主通过可替换的 WebClient 实现发起请求。
/// 仅支持 http:// 和 https:// 协议，防止 SSRF 滥用内部地址。
/// </summary>
[Tool(
    id: "http_fetch",
    name: "HTTP Fetch",
    description: "Make an HTTP/HTTPS request and return raw, Markdown, plain text, or JSON-formatted response content.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.RequiresNetwork)]
public sealed class HttpFetchSkill : PuddingToolBase<HttpFetchArgs>
{
    private readonly IWebClient _webClient;
    private readonly IHttpFetchContentFormatter _formatter;
    private readonly ILogger<HttpFetchSkill> _logger;

    private const int DefaultMaxResponseChars = 8192;

    public HttpFetchSkill(
        IWebClient webClient,
        IHttpFetchContentFormatter formatter,
        ILogger<HttpFetchSkill> logger)
    {
        _webClient = webClient;
        _formatter = formatter;
        _logger = logger;
    }

    

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        HttpFetchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var result = await ExecuteArgsAsync(args, context.WorkspaceId, context.SessionId, context.AgentInstanceId, ct);

        return new ToolExecutionResult
        {
            Success = result.Success,
            Output = result.Output,
            Error = result.Error,
            ExitCode = result.ExitCode,
        };
    }

        private async Task<SkillResult> ExecuteArgsAsync(
        HttpFetchArgs args,
        string workspaceId,
        string sessionId,
        string agentInstanceId,
        CancellationToken ct)
    {
        var url = args.Url?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url))
            return Fail("URL is required.");

        // Security: only http/https — block SSRF to internal metadata endpoints etc.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Fail("Only http:// and https:// URLs are supported.");

        var method = string.IsNullOrWhiteSpace(args.Method)
            ? "GET"
            : args.Method.Trim().ToUpperInvariant();
        if (!IsSupportedMethod(method))
            return Fail($"Unsupported HTTP method '{method}'.");

        _logger.LogInformation("[HttpFetchSkill] agent={Agent} {Method} {Url}",
            agentInstanceId, method, url);

        try
        {
            var response = await _webClient.SendAsync(new WebClientRequest
            {
                Url = url,
                Method = method,
                Headers = ParseHeaders(args.Headers),
                Body = args.Body,
                ContentType = string.IsNullOrWhiteSpace(args.ContentType)
                    ? "application/json"
                    : args.ContentType,
                TimeoutSeconds = args.TimeoutSeconds,
                CookieScope = args.CookieScope,
                CookieKey = BuildCookieKey(args.CookieScope, workspaceId, sessionId, agentInstanceId),
            }, ct);

            var formatted = _formatter.Format(response, new HttpFetchFormatOptions
            {
                OutputFormat = args.OutputFormat ?? "markdown",
                MaxResponseChars = args.MaxResponseChars ?? DefaultMaxResponseChars,
                IncludeHeaders = args.IncludeHeaders ?? false,
            });

            var statusLine = $"HTTP {response.StatusCode} {response.ReasonPhrase}";
            var success = response.StatusCode is >= 200 and <= 299;
            return new SkillResult
            {
                Success = success,
                Output = formatted.Output,
                ExitCode = response.StatusCode,
                Error = success ? null : statusLine,
            };
        }
        catch (TaskCanceledException)
        {
            return Fail("Request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HttpFetchSkill] Request failed for agent={Agent} url={Url}",
                agentInstanceId, url);
            return Fail(ex.Message);
        }
    }

    

    private static IReadOnlyDictionary<string, string> ParseHeaders(JsonElement? headers)
    {
        if (headers is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var element = headers.Value;
        if (element.ValueKind == JsonValueKind.String)
            element = ParseJsonObjectString(element.GetString());

        if (element.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }

    private static JsonElement? ParseLegacyJsonElement(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return ParseJsonObjectString(value);
    }

    private static JsonElement ParseJsonObjectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static bool? ParseBool(string? value)
        => bool.TryParse(value, out var parsed) ? parsed : null;

    private static bool IsSupportedMethod(string method)
        => method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";

    private static string? BuildCookieKey(
        string? cookieScope,
        string workspaceId,
        string sessionId,
        string agentInstanceId)
    {
        if (!string.Equals(cookieScope, "session", StringComparison.OrdinalIgnoreCase))
            return null;

        return $"{workspaceId}:{sessionId}:{agentInstanceId}";
    }

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };
}

public sealed record HttpFetchArgs
{
    [ToolParam("HTTP or HTTPS URL to request.")]
    public required string Url { get; init; }

    [ToolParam("HTTP method: GET or POST. Default: GET.")]
    public string? Method { get; init; }

    [ToolParam("HTTP request headers as a JSON object.")]
    public JsonElement? Headers { get; init; }

    [ToolParam("Request body for methods that support a body.")]
    public string? Body { get; init; }

    [ToolParam("Request body content type. Default: application/json.")]
    public string? ContentType { get; init; }

    [ToolParam("Request timeout in seconds. Default is configured by the web client.")]
    public int? TimeoutSeconds { get; init; }

    [ToolParam("Output format: markdown, text, raw, or json. Default: markdown.")]
    public string? OutputFormat { get; init; }

    [ToolParam("Maximum response characters to return. Default: 8192.")]
    public int? MaxResponseChars { get; init; }

    [ToolParam("Whether to include response headers in the output. Default: false.")]
    public bool? IncludeHeaders { get; init; }

    [ToolParam("Cookie scope hint for the web client: none or session. Default: none.")]
    public string? CookieScope { get; init; }
}
