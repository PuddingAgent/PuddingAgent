using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>Search the web through AnySearch's unified search API.</summary>
[Tool(
    id: "anysearch_search",
    name: "AnySearch Search",
    description: "Search web, documentation, news, and domain-specific sources through AnySearch. Returns titles, URLs, snippets, and cleaned content.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 55)]
public sealed class AnySearchSearchTool : PuddingToolBase<AnySearchSearchArgs>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IWebClient _webClient;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<AnySearchSearchTool> _logger;

    public AnySearchSearchTool(
        IWebClient webClient,
        ILogger<AnySearchSearchTool> logger)
        : this(webClient, ResolveDefaultDataPaths(), logger)
    {
    }

    public AnySearchSearchTool(
        IWebClient webClient,
        PuddingDataPaths paths,
        ILogger<AnySearchSearchTool> logger)
    {
        _webClient = webClient;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AnySearchSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail("Query is required.");

        var configResult = LoadConfig();
        if (!configResult.Success)
            return ToolExecutionResult.Fail(configResult.Error ?? "AnySearch configuration is invalid.");

        var config = configResult.Config!;
        if (!config.Enabled)
            return ToolExecutionResult.Fail("AnySearch provider is disabled in search.providers.json.");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return ToolExecutionResult.Fail(
                $"AnySearch API key is not configured. Set anysearch.apiKey in {_paths.SystemConfigFile("search.providers.json")}.");
        }

        var maxResults = Math.Clamp(args.MaxResults ?? 10, 1, 100);
        var endpoint = BuildSearchEndpoint(config.BaseUrl);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            headers["Authorization"] = $"Bearer {config.ApiKey.Trim()}";

        var body = JsonSerializer.Serialize(new AnySearchApiRequest
        {
            Query = query,
            MaxResults = maxResults,
            Domain = NormalizeOptional(args.Domain),
            Tag = NormalizeOptional(args.Tag),
            ContentTypes = args.ContentTypes is { Count: > 0 }
                ? args.ContentTypes.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray()
                : null,
            Zone = NormalizeOptional(args.Zone),
            Language = NormalizeOptional(args.Language),
            Params = args.Params,
        }, s_jsonOptions);

        _logger.LogInformation(
            "[AnySearchSearchTool] agent={Agent} query={Query} maxResults={MaxResults}",
            context.AgentInstanceId,
            query,
            maxResults);

        try
        {
            var response = await _webClient.SendAsync(new WebClientRequest
            {
                Url = endpoint,
                Method = "POST",
                Headers = headers,
                Body = body,
                ContentType = "application/json",
                TimeoutSeconds = config.TimeoutSeconds,
            }, ct);

            return ParseResponse(response, query);
        }
        catch (TaskCanceledException)
        {
            return ToolExecutionResult.Fail("AnySearch request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AnySearchSearchTool] request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"AnySearch request failed: {ex.Message}");
        }
    }

    private AnySearchProviderConfigLoadResult LoadConfig()
    {
        var path = _paths.SystemConfigFile("search.providers.json");
        if (!File.Exists(path))
        {
            return AnySearchProviderConfigLoadResult.Fail(
                $"AnySearch API key is not configured. Config file not found: {path}.");
        }

        try
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<AnySearchProvidersConfig>(json, s_jsonOptions);
            if (root?.AnySearch is null)
            {
                return AnySearchProviderConfigLoadResult.Fail(
                    $"AnySearch API key is not configured. Missing anysearch section in {path}.");
            }

            return AnySearchProviderConfigLoadResult.Ok(root.AnySearch);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[AnySearchSearchTool] failed to read config path={Path}", path);
            return AnySearchProviderConfigLoadResult.Fail(
                $"Failed to read AnySearch config file {path}: {ex.Message}");
        }
    }

    private static PuddingDataPaths ResolveDefaultDataPaths()
    {
        var root = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "data");

        return PuddingDataPaths.FromRoot(root);
    }

    private static ToolExecutionResult ParseResponse(WebClientResponse response, string query)
    {
        AnySearchApiResponse? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<AnySearchApiResponse>(response.Body, s_jsonOptions);
        }
        catch (JsonException)
        {
            // Non-JSON upstream errors are handled below with HTTP status context.
        }

        if (response.StatusCode is < 200 or > 299)
        {
            var requestId = payload?.Data?.RequestId;
            var message = payload?.Message ?? Truncate(response.Body, 512);
            var suffix = string.IsNullOrWhiteSpace(requestId) ? "" : $" request_id={requestId}";
            return ToolExecutionResult.Fail($"HTTP {response.StatusCode} {response.ReasonPhrase}: {message}{suffix}", response.StatusCode);
        }

        if (payload is null)
            return ToolExecutionResult.Fail("AnySearch returned invalid JSON.");

        if (payload.Code != 0)
        {
            var requestId = payload.Data?.RequestId;
            var suffix = string.IsNullOrWhiteSpace(requestId) ? "" : $" request_id={requestId}";
            return ToolExecutionResult.Fail($"AnySearch error code={payload.Code}: {payload.Message}{suffix}");
        }

        var data = payload.Data;
        var results = data?.Results ?? [];
        if (results.Count == 0)
            return ToolExecutionResult.Ok($"AnySearch results for: \"{query}\"\n\n(no results)");

        var sb = new StringBuilder();
        sb.AppendLine($"AnySearch results for: \"{query}\"");
        if (data?.Metadata is not null)
        {
            sb.AppendLine(
                $"request_id={data.Metadata.RequestId ?? ""} total_results={data.Metadata.TotalResults?.ToString() ?? "unknown"} search_time_ms={data.Metadata.SearchTimeMs?.ToString() ?? "unknown"}");
        }

        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            sb.AppendLine();
            sb.AppendLine($"{i + 1}. {item.Title ?? "(untitled)"}");
            if (!string.IsNullOrWhiteSpace(item.Url))
                sb.AppendLine($"URL: {item.Url}");
            if (!string.IsNullOrWhiteSpace(item.Snippet))
                sb.AppendLine($"Snippet: {item.Snippet}");
            if (!string.IsNullOrWhiteSpace(item.Content))
                sb.AppendLine($"Content: {Truncate(item.Content, 1000)}");
        }

        return ToolExecutionResult.Ok(sb.ToString().TrimEnd());
    }

    private static string BuildSearchEndpoint(string? baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl)
            ? AnySearchProviderConfig.DefaultBaseUrl
            : baseUrl.Trim();

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = AnySearchProviderConfig.DefaultBaseUrl;
        }

        return value.TrimEnd('/') + "/v1/search";
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;

        return value[..maxChars] + $"... (truncated at {maxChars} chars)";
    }
}

public sealed record AnySearchSearchArgs
{
    [ToolParam("Search query.")]
    public required string Query { get; init; }

    [ToolParam("Number of results to return, 1-100. Default: 10.")]
    [JsonPropertyName("max_results")]
    public int? MaxResults { get; init; }

    [ToolParam("Optional AnySearch domain filter, for example general, code, academic, legal, finance, or health.")]
    public string? Domain { get; init; }

    [ToolParam("Optional sub-domain capability tag, for example code.doc.")]
    public string? Tag { get; init; }

    [ToolParam("Optional content type filters, for example web, news, or doc.")]
    [JsonPropertyName("content_types")]
    public IReadOnlyList<string>? ContentTypes { get; init; }

    [ToolParam("Optional region: cn or intl.")]
    public string? Zone { get; init; }

    [ToolParam("Optional preferred language, for example zh-CN or en.")]
    public string? Language { get; init; }

    [ToolParam("Optional extended AnySearch parameters object.")]
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

internal sealed record AnySearchProvidersConfig
{
    [JsonPropertyName("anysearch")]
    public AnySearchProviderConfig? AnySearch { get; init; }
}

internal sealed record AnySearchProviderConfig
{
    public const string DefaultBaseUrl = "https://api.anysearch.com";

    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string? ApiKey { get; init; }
    public int? TimeoutSeconds { get; init; } = 30;
}

internal sealed record AnySearchProviderConfigLoadResult(
    bool Success,
    AnySearchProviderConfig? Config,
    string? Error)
{
    public static AnySearchProviderConfigLoadResult Ok(AnySearchProviderConfig config) =>
        new(true, config, null);

    public static AnySearchProviderConfigLoadResult Fail(string error) =>
        new(false, null, error);
}

internal sealed record AnySearchApiRequest
{
    public required string Query { get; init; }

    [JsonPropertyName("max_results")]
    public required int MaxResults { get; init; }

    public string? Domain { get; init; }
    public string? Tag { get; init; }

    [JsonPropertyName("content_types")]
    public IReadOnlyList<string>? ContentTypes { get; init; }

    public string? Zone { get; init; }
    public string? Language { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

internal sealed record AnySearchApiResponse
{
    public int Code { get; init; }
    public string Message { get; init; } = string.Empty;
    public AnySearchApiData? Data { get; init; }
}

internal sealed record AnySearchApiData
{
    public IReadOnlyList<AnySearchResultItem> Results { get; init; } = [];
    public AnySearchMetadata? Metadata { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

internal sealed record AnySearchResultItem
{
    public string? Title { get; init; }
    public string? Url { get; init; }
    public string? Snippet { get; init; }
    public string? Content { get; init; }
}

internal sealed record AnySearchMetadata
{
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("total_results")]
    public int? TotalResults { get; init; }

    [JsonPropertyName("search_time_ms")]
    public int? SearchTimeMs { get; init; }
}
