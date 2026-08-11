using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Search the entire web through Zhihu's global search API, with optional advanced filtering
/// by domain (host=="...") and publish time (publish_time&gt;=timestamp) and a selectable
/// search database (all/realtime/static). Shares the zhihu_search.apiKey from
/// search.providers.json with ZhihuSearchTool.
/// </summary>
[Tool(
    id: "zhihu_global_search",
    name: "Zhihu Global Search",
    description: "Search the entire web through Zhihu's global search API with advanced filtering by domain and publish time.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 58)]
public sealed class ZhihuGlobalSearchTool : PuddingToolBase<ZhihuGlobalSearchArgs>
{
    internal const string DefaultEndpoint = "https://developer.zhihu.com/api/v1/content/global_search";
    internal const int MaxCount = 20;

    private static readonly System.Collections.Generic.HashSet<string> s_validSearchDbs = new(
        new[] { "all", "realtime", "static" },
        StringComparer.OrdinalIgnoreCase);

    private readonly IWebClient _webClient;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<ZhihuGlobalSearchTool> _logger;

    public ZhihuGlobalSearchTool(
        IWebClient webClient,
        ILogger<ZhihuGlobalSearchTool> logger)
        : this(webClient, ZhihuSearchShared.ResolveDefaultDataPaths(), logger)
    {
    }

    public ZhihuGlobalSearchTool(
        IWebClient webClient,
        PuddingDataPaths paths,
        ILogger<ZhihuGlobalSearchTool> logger)
    {
        _webClient = webClient;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ZhihuGlobalSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail("Query is required.");

        var count = Math.Clamp(args.Count ?? 10, 1, MaxCount);
        var searchDb = NormalizeSearchDb(args.SearchDB);
        if (searchDb is null && !string.IsNullOrWhiteSpace(args.SearchDB))
        {
            return ToolExecutionResult.Fail(
                "SearchDB must be one of: all, realtime, static.");
        }

        var configResult = ZhihuSearchConfigLoader.Load(_paths, _logger, nameof(ZhihuGlobalSearchTool));
        if (!configResult.Success)
            return ToolExecutionResult.Fail(configResult.Error ?? "Zhihu configuration is invalid.");

        var config = configResult.Config!;
        if (!config.Enabled)
            return ToolExecutionResult.Fail("Zhihu Search provider is disabled in search.providers.json.");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return ToolExecutionResult.Fail(
                $"Zhihu API key is not configured. Set zhihu_search.apiKey in {_paths.SystemConfigFile(ZhihuSearchShared.ConfigFileName)}.");
        }

        var endpoint = BuildEndpoint(config.BaseUrl, query, count, args.Filter, searchDb);
        var headers = BuildHeaders(config.ApiKey.Trim());

        _logger.LogInformation(
            "[ZhihuGlobalSearchTool] agent={Agent} queryLength={QueryLength} count={Count} searchDb={SearchDb} hasFilter={HasFilter}",
            context.AgentInstanceId,
            query.Length,
            count,
            searchDb ?? "all",
            !string.IsNullOrWhiteSpace(args.Filter));

        try
        {
            var response = await _webClient.SendAsync(new WebClientRequest
            {
                Url = endpoint,
                Method = "GET",
                Headers = headers,
                ContentType = "application/json",
                TimeoutSeconds = config.TimeoutSeconds,
            }, ct);

            return ZhihuSearchResponseParser.Parse(response, query, "Zhihu global search results", includeRanking: false);
        }
        catch (TaskCanceledException)
        {
            return ToolExecutionResult.Fail("Zhihu global search request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ZhihuGlobalSearchTool] request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"Zhihu global search request failed: {ex.Message}");
        }
    }

    private static string? NormalizeSearchDb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return s_validSearchDbs.Contains(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    private static string BuildEndpoint(
        string? baseUrl,
        string query,
        int count,
        string? filter,
        string? searchDb)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? DefaultEndpoint : baseUrl.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = DefaultEndpoint;
        }

        var sb = new StringBuilder(value.TrimEnd('/'));
        sb.Append("?Query=").Append(Uri.EscapeDataString(query));
        sb.Append("&Count=").Append(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(filter))
            sb.Append("&Filter=").Append(Uri.EscapeDataString(filter.Trim()));
        if (!string.IsNullOrWhiteSpace(searchDb))
            sb.Append("&SearchDB=").Append(Uri.EscapeDataString(searchDb));

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(string apiKey) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {apiKey}",
            ["X-Request-Timestamp"] = DateTimeOffset.UtcNow
                .ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
}

public sealed record ZhihuGlobalSearchArgs
{
    [ToolParam("Search query.")]
    public required string Query { get; init; }

    [ToolParam("Number of results to return, 1-20. Default: 10.")]
    public int? Count { get; init; }

    [ToolParam("Advanced filter expression, for example host==\"zhihu.com\" AND publish_time>=1710000000. Supports AND/OR and parentheses.")]
    public string? Filter { get; init; }

    [ToolParam("Search database: all, realtime, or static. Default: all.")]
    [JsonPropertyName("search_db")]
    public string? SearchDB { get; init; }
}
