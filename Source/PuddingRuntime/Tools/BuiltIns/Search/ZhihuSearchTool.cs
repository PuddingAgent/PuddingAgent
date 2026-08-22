using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Search Zhihu site-internal content (questions, answers, articles) through the Zhihu Open
/// Platform developer API. Authentication uses a Bearer apiKey from the zhihu_search section
/// of search.providers.json plus a Unix request timestamp header.
/// </summary>
[Tool(
    id: "zhihu_search",
    name: "Zhihu Search",
    description: "搜索知乎站内内容（问题、回答、文章，站内搜索）。返回标题、URL、摘要与元数据。【何时用】需要知乎站内的问答、文章等社区内容（如产品评价、经验分享、观点讨论）时使用。【怎么用】传 query 即可；count（1-10，默认10）控制返回条数。【坑】仅搜索知乎站内内容，全网搜索请用 zhihu_global_search 或 doubao_search/anysearch_search；需配置 zhihu_search.apiKey；返回为摘要与元数据，重要观点请打开原文核实。",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 57)]
public sealed class ZhihuSearchTool : PuddingToolBase<ZhihuSearchArgs>
{
    internal const string DefaultEndpoint = "https://developer.zhihu.com/api/v1/content/zhihu_search";
    internal const int MaxCount = 10;

    private readonly IWebClient _webClient;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<ZhihuSearchTool> _logger;

    public ZhihuSearchTool(
        IWebClient webClient,
        ILogger<ZhihuSearchTool> logger)
        : this(webClient, ZhihuSearchShared.ResolveDefaultDataPaths(), logger)
    {
    }

    public ZhihuSearchTool(
        IWebClient webClient,
        PuddingDataPaths paths,
        ILogger<ZhihuSearchTool> logger)
    {
        _webClient = webClient;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ZhihuSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail("Query is required.");

        var count = Math.Clamp(args.Count ?? 10, 1, MaxCount);

        var configResult = ZhihuSearchConfigLoader.Load(_paths, _logger, nameof(ZhihuSearchTool));
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

        var endpoint = BuildEndpoint(config.BaseUrl, query, count);
        var headers = BuildHeaders(config.ApiKey.Trim());

        _logger.LogInformation(
            "[ZhihuSearchTool] agent={Agent} queryLength={QueryLength} count={Count}",
            context.AgentInstanceId,
            query.Length,
            count);

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

            return ZhihuSearchResponseParser.Parse(response, query, "Zhihu search results", includeRanking: true);
        }
        catch (TaskCanceledException)
        {
            return ToolExecutionResult.Fail("Zhihu search request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ZhihuSearchTool] request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"Zhihu search request failed: {ex.Message}");
        }
    }

    private static string BuildEndpoint(string? baseUrl, string query, int count)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? DefaultEndpoint : baseUrl.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = DefaultEndpoint;
        }

        return $"{value.TrimEnd('/')}?Query={Uri.EscapeDataString(query)}&Count={count}";
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

public sealed record ZhihuSearchArgs
{
    [ToolParam("Search query.")]
    public required string Query { get; init; }

    [ToolParam("Number of results to return, 1-10. Default: 10.")]
    public int? Count { get; init; }
}
