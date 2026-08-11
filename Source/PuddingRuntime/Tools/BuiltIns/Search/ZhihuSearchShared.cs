using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Shared configuration loading, response DTOs, and Markdown rendering helpers for the two
/// Zhihu Open Platform search tools (zhihu_search and zhihu_global_search). Both tools read
/// the same "zhihu_search" section from search.providers.json and share the same apiKey.
/// </summary>
internal static class ZhihuSearchShared
{
    internal const string ConfigFileName = "search.providers.json";
    internal const int MaxRenderedOutputChars = 60_000;
    internal const int MaxSnippetChars = 300;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly System.Text.RegularExpressions.Regex s_htmlTagRegex = new(
        "<[^>]+>",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex s_whitespaceRegex = new(
        @"\s{2,}",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Resolves the default data root used when no paths are injected.</summary>
    internal static PuddingDataPaths ResolveDefaultDataPaths()
    {
        var root = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "data");

        return PuddingDataPaths.FromRoot(root);
    }

    /// <summary>Strips HTML tags, decodes entities, and collapses whitespace.</summary>
    internal static string CleanContentText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decoded = System.Net.WebUtility.HtmlDecode(s_htmlTagRegex.Replace(value, string.Empty));
        return s_whitespaceRegex.Replace(decoded.Trim(), " ").Trim();
    }

    /// <summary>Sanitizes a value so it can be embedded in a Markdown table cell.</summary>
    internal static string EscapeTableCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return s_whitespaceRegex
            .Replace(value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim(), " ")
            .Trim();
    }

    /// <summary>Formats a Unix timestamp (seconds) as a UTC date, or "-" when absent/invalid.</summary>
    internal static string FormatEditTime(long? unixSeconds)
    {
        if (unixSeconds is null or <= 0)
            return "-";

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value)
                .UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "-";
        }
    }

    internal static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;

        return value[..maxChars] + $"... (truncated at {maxChars} chars)";
    }
}

/// <summary>Root of search.providers.json. Only the zhihu_search section is consumed here.</summary>
internal sealed record ZhihuSearchProvidersConfig
{
    [JsonPropertyName("zhihu_search")]
    public ZhihuSearchProviderConfig? ZhihuSearch { get; init; }
}

/// <summary>The zhihu_search provider section shared by both Zhihu search tools.</summary>
internal sealed record ZhihuSearchProviderConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>Optional endpoint override; falls back to each tool's default endpoint.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Zhihu Open Platform API key (Bearer token).</summary>
    public string? ApiKey { get; init; }

    public int TimeoutSeconds { get; init; } = 15;
}

internal sealed record ZhihuSearchProviderConfigLoadResult(
    bool Success,
    ZhihuSearchProviderConfig? Config,
    string? Error)
{
    public static ZhihuSearchProviderConfigLoadResult Ok(ZhihuSearchProviderConfig config) =>
        new(true, config, null);

    public static ZhihuSearchProviderConfigLoadResult Fail(string error) =>
        new(false, null, error);
}

/// <summary>
/// Static config-loading helper shared by ZhihuSearchTool and ZhihuGlobalSearchTool.
/// Reads the "zhihu_search" section from <c>search.providers.json</c>.
/// </summary>
internal static class ZhihuSearchConfigLoader
{
    public static ZhihuSearchProviderConfigLoadResult Load(
        PuddingDataPaths paths,
        ILogger logger,
        string toolName)
    {
        var path = paths.SystemConfigFile(ZhihuSearchShared.ConfigFileName);
        if (!File.Exists(path))
        {
            return ZhihuSearchProviderConfigLoadResult.Fail(
                $"Zhihu API key is not configured. Config file not found: {path}.");
        }

        try
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<ZhihuSearchProvidersConfig>(json, ZhihuSearchShared.JsonOptions);
            if (root?.ZhihuSearch is null)
            {
                return ZhihuSearchProviderConfigLoadResult.Fail(
                    $"Zhihu API key is not configured. Missing zhihu_search section in {path}.");
            }

            return ZhihuSearchProviderConfigLoadResult.Ok(root.ZhihuSearch);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[{ToolName}] failed to read config path={Path}", toolName, path);
            return ZhihuSearchProviderConfigLoadResult.Fail(
                $"Failed to read Zhihu config file {path}: {ex.Message}");
        }
    }
}

/// <summary>Standard Zhihu Open Platform response envelope: Code / Message / Data.</summary>
internal sealed record ZhihuSearchApiResponse
{
    public int Code { get; init; }
    public string Message { get; init; } = string.Empty;
    public ZhihuSearchApiData? Data { get; init; }
}

/// <summary>Search payload shared by the site-internal and global search APIs.</summary>
internal sealed record ZhihuSearchApiData
{
    public bool HasMore { get; init; }

    [JsonPropertyName("SearchHashId")]
    public string? SearchHashId { get; init; }

    [JsonPropertyName("EmptyReason")]
    public string? EmptyReason { get; init; }

    public IReadOnlyList<ZhihuSearchItem> Items { get; init; } = [];
}

/// <summary>
/// A single search result item. Field names match the Zhihu Open Platform API (case-insensitive
/// JSON binding). EditTime is int32 on zhihu_search and int64 on global_search; long covers both.
/// </summary>
internal sealed record ZhihuSearchItem
{
    public string? Title { get; init; }
    public string? ContentType { get; init; }
    public string? ContentID { get; init; }
    public string? ContentText { get; init; }
    public string? Url { get; init; }
    public long? CommentCount { get; init; }
    public long? VoteUpCount { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorAvatar { get; init; }
    public string? AuthorBadge { get; init; }
    public string? AuthorBadgeText { get; init; }
    public long? EditTime { get; init; }
    public IReadOnlyList<ZhihuCommentInfo>? CommentInfoList { get; init; }
    public string? AuthorityLevel { get; init; }
    public float? RankingScore { get; init; }
}

/// <summary>Comment summary attached to a search result. Shape is informational only.</summary>
internal sealed record ZhihuCommentInfo
{
    public string? Content { get; init; }
    public string? AuthorName { get; init; }
    public long? CommentCount { get; init; }
}

/// <summary>
/// Parses Zhihu search API responses into a ToolExecutionResult with a Markdown table
/// (title/type/author/votes/comments/edited/url, plus ranking for site-internal search)
/// followed by a snippets list.
/// </summary>
internal static class ZhihuSearchResponseParser
{
    public static ToolExecutionResult Parse(
        WebClientResponse response,
        string query,
        string headerText,
        bool includeRanking)
    {
        ZhihuSearchApiResponse? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<ZhihuSearchApiResponse>(response.Body, ZhihuSearchShared.JsonOptions);
        }
        catch (JsonException)
        {
            // Non-JSON upstream errors are handled below with HTTP status context.
        }

        if (response.StatusCode is < 200 or > 299)
        {
            var message = FormatProviderError(payload) ?? ZhihuSearchShared.Truncate(response.Body, 512);
            return ToolExecutionResult.Fail(
                $"HTTP {response.StatusCode} {response.ReasonPhrase}: {message}",
                response.StatusCode);
        }

        if (payload is null)
            return ToolExecutionResult.Fail("Zhihu returned invalid JSON.");

        if (payload.Code != 0)
            return ToolExecutionResult.Fail($"Zhihu error code={payload.Code}: {payload.Message}");

        var data = payload.Data;
        var items = data?.Items ?? [];
        var sb = new StringBuilder();
        sb.AppendLine($"{headerText} for: \"{query}\"");

        var metadataParts = new List<string>(3);
        if (data is not null)
        {
            if (data.HasMore)
                metadataParts.Add("has_more=true");
            if (!string.IsNullOrWhiteSpace(data.SearchHashId))
                metadataParts.Add($"search_hash_id={data.SearchHashId}");
            if (!string.IsNullOrWhiteSpace(data.EmptyReason))
                metadataParts.Add($"empty_reason={data.EmptyReason}");
        }

        if (metadataParts.Count > 0)
            sb.AppendLine(string.Join(" ", metadataParts));

        if (items.Count == 0)
        {
            sb.AppendLine();
            sb.Append("(no results)");
            return ToolExecutionResult.Ok(
                ZhihuSearchShared.Truncate(sb.ToString().TrimEnd(), ZhihuSearchShared.MaxRenderedOutputChars));
        }

        AppendTable(sb, items, includeRanking);
        AppendSnippets(sb, items);

        return ToolExecutionResult.Ok(
            ZhihuSearchShared.Truncate(sb.ToString().TrimEnd(), ZhihuSearchShared.MaxRenderedOutputChars));
    }

    private static void AppendTable(StringBuilder sb, IReadOnlyList<ZhihuSearchItem> items, bool includeRanking)
    {
        sb.AppendLine();
        sb.AppendLine(includeRanking
            ? "| # | Title | Type | Author | Votes | Comments | Edited | Ranking | URL |"
            : "| # | Title | Type | Author | Votes | Comments | Edited | URL |");
        sb.AppendLine(includeRanking
            ? "|---|-------|------|--------|-------|----------|--------|---------|-----|"
            : "|---|-------|------|--------|-------|----------|--------|-----|");

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var title = ZhihuSearchShared.EscapeTableCell(item.Title);
            if (string.IsNullOrWhiteSpace(title))
                title = "(untitled)";
            var type = ZhihuSearchShared.EscapeTableCell(item.ContentType);
            if (string.IsNullOrWhiteSpace(type))
                type = "-";
            var url = ZhihuSearchShared.EscapeTableCell(item.Url);
            if (string.IsNullOrWhiteSpace(url))
                url = "-";

            var row = includeRanking
                ? $"| {i + 1} | {title} | {type} | {BuildAuthor(item)} | {FormatCount(item.VoteUpCount)} | {FormatCount(item.CommentCount)} | {ZhihuSearchShared.FormatEditTime(item.EditTime)} | {FormatRanking(item.RankingScore)} | {url} |"
                : $"| {i + 1} | {title} | {type} | {BuildAuthor(item)} | {FormatCount(item.VoteUpCount)} | {FormatCount(item.CommentCount)} | {ZhihuSearchShared.FormatEditTime(item.EditTime)} | {url} |";
            sb.AppendLine(row);
        }
    }

    private static void AppendSnippets(StringBuilder sb, IReadOnlyList<ZhihuSearchItem> items)
    {
        if (!items.Any(item => !string.IsNullOrWhiteSpace(item.ContentText)))
            return;

        sb.AppendLine();
        sb.AppendLine("Snippets:");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.ContentText))
                continue;

            var snippet = ZhihuSearchShared.CleanContentText(
                ZhihuSearchShared.Truncate(item.ContentText, ZhihuSearchShared.MaxSnippetChars));
            if (!string.IsNullOrWhiteSpace(snippet))
                sb.AppendLine($"{i + 1}. {snippet}");
        }
    }

    private static string BuildAuthor(ZhihuSearchItem item)
    {
        var name = ZhihuSearchShared.EscapeTableCell(item.AuthorName);
        var badge = ZhihuSearchShared.EscapeTableCell(item.AuthorBadgeText ?? item.AuthorBadge);

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(badge))
            return "-";
        if (string.IsNullOrWhiteSpace(name))
            return badge;
        if (string.IsNullOrWhiteSpace(badge))
            return name;
        return $"{name} ({badge})";
    }

    private static string FormatCount(long? count) => count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string FormatRanking(float? ranking) =>
        ranking?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string? FormatProviderError(ZhihuSearchApiResponse? payload)
    {
        if (payload is null)
            return null;

        if (payload.Code != 0)
            return $"Zhihu error code={payload.Code}: {payload.Message}";

        return string.IsNullOrWhiteSpace(payload.Message) ? null : payload.Message.Trim();
    }
}
