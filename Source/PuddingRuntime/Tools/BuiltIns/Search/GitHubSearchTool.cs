using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Search GitHub repositories, code, issues, and commits through the GitHub REST API.
/// Used to verify official documentation, dependency compatibility, and community discussions
/// before making code decisions — avoiding the "judge by memory" trap.
/// </summary>
[Tool(
    id: "github_search",
    name: "GitHub Search",
    description: "Search GitHub repositories, code, issues, and commits via GitHub REST API. Returns titles, URLs, and descriptions.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 56)]
public sealed class GitHubSearchTool : PuddingToolBase<GitHubSearchArgs>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly HashSet<string> s_validSearchTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "repositories", "code", "issues", "commits", "topics"
    };

    private readonly IWebClient _webClient;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<GitHubSearchTool> _logger;

    public GitHubSearchTool(
        IWebClient webClient,
        ILogger<GitHubSearchTool> logger)
        : this(webClient, ResolveDefaultDataPaths(), logger)
    {
    }

    public GitHubSearchTool(
        IWebClient webClient,
        PuddingDataPaths paths,
        ILogger<GitHubSearchTool> logger)
    {
        _webClient = webClient;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GitHubSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail("Query is required.");

        var searchType = NormalizeSearchType(args.Type);
        var maxResults = Math.Clamp(args.MaxResults ?? 10, 1, 100);

        var config = LoadConfig();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github.v3+json",
            ["User-Agent"] = "PuddingAgent/1.0",
        };

        if (!string.IsNullOrWhiteSpace(config.Token))
            headers["Authorization"] = $"Bearer {config.Token.Trim()}";

        var encodedQuery = Uri.EscapeDataString(query);
        var endpoint = $"{config.BaseUrl}/search/{searchType}?q={encodedQuery}&per_page={maxResults}";

        _logger.LogInformation(
            "[GitHubSearchTool] agent={Agent} type={Type} query={Query}",
            context.AgentInstanceId, searchType, query);

        try
        {
            var response = await _webClient.SendAsync(new WebClientRequest
            {
                Url = endpoint,
                Method = "GET",
                Headers = headers,
                TimeoutSeconds = config.TimeoutSeconds,
            }, ct);

            return ParseResponse(response, query, searchType);
        }
        catch (TaskCanceledException)
        {
            return ToolExecutionResult.Fail("GitHub search request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitHubSearchTool] request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"GitHub search request failed: {ex.Message}");
        }
    }

    private GitHubSearchConfig LoadConfig()
    {
        var path = _paths.SystemConfigFile("search.providers.json");
        if (!File.Exists(path))
            return GitHubSearchConfig.Default;

        try
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<SearchProvidersConfig>(json, s_jsonOptions);
            return root?.GitHub ?? GitHubSearchConfig.Default;
        }
        catch
        {
            return GitHubSearchConfig.Default;
        }
    }

    private static ToolExecutionResult ParseResponse(
        WebClientResponse response, string query, string searchType)
    {
        if (response.StatusCode is 403)
            return ToolExecutionResult.Fail(
                "GitHub API rate limit exceeded. " +
                "Configure a GitHub token in search.providers.json to increase the limit (unauthenticated: 10 req/min, authenticated: 30 req/min).");
        if (response.StatusCode is 422)
            return ToolExecutionResult.Fail(
                $"GitHub validation failed — check query syntax. Only the first 5 qualifiers (language:, org:, repo:) are supported per query.");

        GitHubSearchApiResponse? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubSearchApiResponse>(response.Body, s_jsonOptions);
        }
        catch
        {
            // Non-JSON error response
        }

        if (response.StatusCode is < 200 or > 299)
        {
            var message = payload?.Message ?? Truncate(response.Body, 512);
            return ToolExecutionResult.Fail($"HTTP {response.StatusCode}: {message}");
        }

        if (payload is null)
            return ToolExecutionResult.Fail("GitHub returned invalid JSON.");

        if (payload.TotalCount == 0)
            return ToolExecutionResult.Ok($"GitHub {searchType} search for: \"{query}\"\n\n(no results)");

        var items = payload.Items ?? [];
        var sb = new StringBuilder();
        sb.AppendLine($"GitHub {searchType} search for: \"{query}\"");
        sb.AppendLine($"total_count={payload.TotalCount} incomplete_results={payload.IncompleteResults}");
        sb.AppendLine();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.AppendLine(BuildItemLine(i + 1, item, searchType));
        }

        return ToolExecutionResult.Ok(sb.ToString().TrimEnd());
    }

    private static string BuildItemLine(int index, GitHubSearchItem item, string searchType)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{index}. {item.FullName ?? item.Name ?? "(untitled)"}");

        if (!string.IsNullOrWhiteSpace(item.HtmlUrl))
            sb.AppendLine($"   URL: {item.HtmlUrl}");

        if (!string.IsNullOrWhiteSpace(item.Description))
            sb.AppendLine($"   Description: {item.Description}");

        switch (searchType)
        {
            case "repositories":
                if (item.StargazersCount.HasValue)
                    sb.Append($"   Stars: {item.StargazersCount.Value}");
                if (!string.IsNullOrWhiteSpace(item.Language))
                    sb.Append($" | Language: {item.Language}");
                if (item.UpdatedAt.HasValue)
                    sb.Append($" | Updated: {item.UpdatedAt.Value:yyyy-MM-dd}");
                break;
            case "issues":
                if (!string.IsNullOrWhiteSpace(item.State))
                    sb.Append($"   State: {item.State}");
                if (item.UpdatedAt.HasValue)
                    sb.Append($" | Updated: {item.UpdatedAt.Value:yyyy-MM-dd}");
                break;
            case "code":
                if (!string.IsNullOrWhiteSpace(item.Repository?.FullName))
                    sb.Append($"   Repository: {item.Repository.FullName}");
                if (!string.IsNullOrWhiteSpace(item.Path))
                    sb.Append($" | Path: {item.Path}");
                break;
        }

        sb.AppendLine();
        return sb.ToString().TrimEnd();
    }

    private static string NormalizeSearchType(string? type)
    {
        var value = type?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "repositories";

        return s_validSearchTypes.Contains(value) ? value.ToLowerInvariant() : "repositories";
    }

    private static PuddingDataPaths ResolveDefaultDataPaths()
    {
        var root = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "data");

        return PuddingDataPaths.FromRoot(root);
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;

        return value[..maxChars] + $"... (truncated at {maxChars} chars)";
    }
}

public sealed record GitHubSearchArgs
{
    [ToolParam("Search query. Supports GitHub qualifiers like language:cs, org:dotnet, repo:owner/name.")]
    public required string Query { get; init; }

    [ToolParam("Search type: repositories, code, issues, commits, or topics. Default: repositories.")]
    public string? Type { get; init; }

    [ToolParam("Number of results to return, 1-100. Default: 10.")]
    [JsonPropertyName("max_results")]
    public int? MaxResults { get; init; }
}

internal sealed record SearchProvidersConfig
{
    [JsonPropertyName("github")]
    public GitHubSearchConfig? GitHub { get; init; }
}

internal sealed record GitHubSearchConfig
{
    public const string DefaultBaseUrl = "https://api.github.com";

    public static readonly GitHubSearchConfig Default = new();

    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string? Token { get; init; }
    public int TimeoutSeconds { get; init; } = 15;
}

internal sealed record GitHubSearchApiResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; init; }

    [JsonPropertyName("incomplete_results")]
    public bool IncompleteResults { get; init; }

    public IReadOnlyList<GitHubSearchItem> Items { get; init; } = [];
    public string? Message { get; init; }
}

internal sealed record GitHubSearchItem
{
    // Common
    public string? Name { get; init; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    public string? Description { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    // Repository
    [JsonPropertyName("stargazers_count")]
    public int? StargazersCount { get; init; }

    public string? Language { get; init; }

    // Issue / PR
    public string? State { get; init; }

    // Code
    public string? Path { get; init; }

    [JsonPropertyName("repository")]
    public GitHubSearchRepo? Repository { get; init; }
}

internal sealed record GitHubSearchRepo
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }
}
