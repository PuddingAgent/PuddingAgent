using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flurl.Http;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>Search the public web through Doubao Search Global.</summary>
[Tool(
    id: "doubao_search",
    name: "Doubao Search",
    description: "Search the public web through Doubao Search Global. Returns ranked sources, text snippets, image URLs, and source metadata.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 56)]
public sealed class DoubaoSearchTool : PuddingToolBase<DoubaoSearchArgs>
{
    internal const string DefaultEndpoint = "https://open.feedcoopapi.com/search_api/global_search";
    internal const int MaxRenderedOutputChars = 60_000;
    private const int MaxRenderedSnippetChars = 6_000;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IWebClient _webClient;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<DoubaoSearchTool> _logger;

    public DoubaoSearchTool(
        IWebClient webClient,
        ILogger<DoubaoSearchTool> logger)
        : this(webClient, ResolveDefaultDataPaths(), logger)
    {
    }

    public DoubaoSearchTool(
        IWebClient webClient,
        PuddingDataPaths paths,
        ILogger<DoubaoSearchTool> logger)
    {
        _webClient = webClient;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        DoubaoSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim() ?? string.Empty;
        var validationError = ValidateArguments(query, args);
        if (validationError is not null)
            return ToolExecutionResult.Fail(validationError);

        var configResult = LoadConfig();
        if (!configResult.Success)
            return ToolExecutionResult.Fail(configResult.Error ?? "Doubao Search configuration is invalid.");

        var config = configResult.Config!;
        if (!config.Enabled)
            return ToolExecutionResult.Fail("Doubao Search provider is disabled in search.providers.json.");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return ToolExecutionResult.Fail(
                $"Doubao Search API key is not configured. Set doubao_search.apiKey in {_paths.SystemConfigFile("search.providers.json")}.");
        }

        var body = JsonSerializer.Serialize(new DoubaoSearchApiRequest
        {
            Query = query,
            DocCount = args.DocCount ?? 10,
            MaxSnippetLength = args.MaxSnippetLength ?? 500,
            MaxImageCountPerDoc = args.MaxImageCountPerDoc ?? 3,
        }, s_jsonOptions);

        _logger.LogInformation(
            "[DoubaoSearchTool] agent={Agent} queryLength={QueryLength} docCount={DocCount}",
            context.AgentInstanceId,
            query.Length,
            args.DocCount ?? 10);

        try
        {
            var response = await _webClient.SendAsync(new WebClientRequest
            {
                Url = DefaultEndpoint,
                Method = "POST",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"Bearer {config.ApiKey.Trim()}",
                },
                Body = body,
                ContentType = "application/json",
                TimeoutSeconds = config.TimeoutSeconds,
            }, ct);

            return ParseResponse(response, query);
        }
        catch (FlurlHttpTimeoutException)
        {
            return ToolExecutionResult.Fail("Doubao Search request timed out.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolExecutionResult.Fail("Doubao Search request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DoubaoSearchTool] request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"Doubao Search request failed: {ex.Message}");
        }
    }

    private static string? ValidateArguments(string query, DoubaoSearchArgs args)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Query is required.";
        if (query.Length > 100)
            return "Query must contain at most 100 characters.";
        if (args.DocCount is < 1 or > 20)
            return "doc_count must be between 1 and 20.";
        if (args.MaxSnippetLength is < 1 or > 3_000)
            return "max_snippet_length must be between 1 and 3000.";
        if (args.MaxImageCountPerDoc is < 1 or > 10)
            return "max_image_count_per_doc must be between 1 and 10.";

        return null;
    }

    private DoubaoSearchProviderConfigLoadResult LoadConfig()
    {
        var path = _paths.SystemConfigFile("search.providers.json");
        if (!File.Exists(path))
        {
            return DoubaoSearchProviderConfigLoadResult.Fail(
                $"Doubao Search API key is not configured. Config file not found: {path}.");
        }

        try
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<DoubaoSearchProvidersConfig>(json, s_jsonOptions);
            if (root?.DoubaoSearch is null)
            {
                return DoubaoSearchProviderConfigLoadResult.Fail(
                    $"Doubao Search API key is not configured. Missing doubao_search section in {path}.");
            }

            return DoubaoSearchProviderConfigLoadResult.Ok(root.DoubaoSearch);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[DoubaoSearchTool] failed to read config path={Path}", path);
            return DoubaoSearchProviderConfigLoadResult.Fail(
                $"Failed to read Doubao Search config file {path}: {ex.Message}");
        }
    }

    private static ToolExecutionResult ParseResponse(WebClientResponse response, string query)
    {
        DoubaoSearchApiResponse? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<DoubaoSearchApiResponse>(response.Body, s_jsonOptions);
        }
        catch (JsonException)
        {
            // Preserve HTTP context below when the upstream body is not JSON.
        }

        var requestId = payload?.ResponseMetadata?.RequestId;
        var metadataError = payload?.ResponseMetadata?.Error;
        var resultErrorCode = payload?.Result?.ErrorCode;
        var resultErrorMessage = payload?.Result?.ErrorMsg;

        if (response.StatusCode is < 200 or > 299)
        {
            var error = FormatProviderError(metadataError, resultErrorCode, resultErrorMessage, requestId)
                        ?? Truncate(response.Body, 512);
            return ToolExecutionResult.Fail(
                $"HTTP {response.StatusCode} {response.ReasonPhrase}: {error}",
                response.StatusCode);
        }

        if (payload is null)
            return ToolExecutionResult.Fail("Doubao Search returned invalid JSON.");

        var providerError = FormatProviderError(metadataError, resultErrorCode, resultErrorMessage, requestId);
        if (providerError is not null)
            return ToolExecutionResult.Fail(providerError);

        if (payload.Result is null)
        {
            return ToolExecutionResult.Fail(
                AppendRequestId("Doubao Search returned no result payload.", requestId));
        }

        var documents = payload.Result.Documents ?? [];
        var sb = new StringBuilder();
        sb.AppendLine($"Doubao Search results for: \"{query}\"");
        sb.AppendLine(
            $"request_id={requestId ?? "unknown"} total_results={payload.Result.TotalDocCount} returned_results={documents.Count}");

        if (documents.Count == 0)
        {
            sb.AppendLine();
            sb.Append("(no results)");
            return ToolExecutionResult.Ok(sb.ToString());
        }

        for (var i = 0; i < documents.Count; i++)
            AppendDocument(sb, documents[i], i);

        return ToolExecutionResult.Ok(Truncate(sb.ToString().TrimEnd(), MaxRenderedOutputChars));
    }

    private static string? FormatProviderError(
        DoubaoSearchMetadataError? metadataError,
        long? resultErrorCode,
        string? resultErrorMessage,
        string? requestId)
    {
        if (metadataError is not null)
        {
            var code = !string.IsNullOrWhiteSpace(metadataError.Code)
                ? metadataError.Code
                : metadataError.CodeN?.ToString() ?? "unknown";
            var message = string.IsNullOrWhiteSpace(metadataError.Message)
                ? "Unknown provider error."
                : metadataError.Message.Trim();
            return AppendRequestId($"Doubao Search error code={code}: {message}", requestId);
        }

        if (resultErrorCode is not null and not 0)
        {
            var message = string.IsNullOrWhiteSpace(resultErrorMessage)
                ? "Unknown provider error."
                : resultErrorMessage.Trim();
            return AppendRequestId($"Doubao Search error code={resultErrorCode}: {message}", requestId);
        }

        return null;
    }

    private static void AppendDocument(StringBuilder sb, DoubaoSearchDocument document, int index)
    {
        sb.AppendLine();
        sb.AppendLine($"{index + 1}. {document.Title ?? "(untitled)"}");
        sb.AppendLine($"Rank: {document.Rank}");
        if (!string.IsNullOrWhiteSpace(document.Url))
            sb.AppendLine($"URL: {document.Url}");
        if (!string.IsNullOrWhiteSpace(document.HostInfo?.Hostname))
            sb.AppendLine($"Source: {document.HostInfo.Hostname}");
        if (!string.IsNullOrWhiteSpace(document.DocumentInfo?.Filetype))
            sb.AppendLine($"Type: {document.DocumentInfo.Filetype}");
        if (!string.IsNullOrWhiteSpace(document.DocumentInfo?.PublishTime))
            sb.AppendLine($"Published: {document.DocumentInfo.PublishTime}");

        var textSnippets = document.Snippet?
            .Where(item => string.Equals(item.Type, "text", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => item.Text!.Trim())
            .ToArray() ?? [];
        if (textSnippets.Length > 0)
            sb.AppendLine($"Snippet: {Truncate(string.Join("\n", textSnippets), MaxRenderedSnippetChars)}");

        var images = document.Snippet?
            .Where(item => string.Equals(item.Type, "image", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(item.Image?.ImageUrl))
            .Select(item => item.Image!)
            .ToArray() ?? [];
        foreach (var image in images)
        {
            var dimensions = image.Width is > 0 && image.Height is > 0
                ? $" ({image.Width}x{image.Height})"
                : string.Empty;
            var alt = string.IsNullOrWhiteSpace(image.Alt) ? string.Empty : $" alt=\"{image.Alt.Trim()}\"";
            sb.AppendLine($"Image: {image.ImageUrl}{dimensions}{alt}");
        }
    }

    private static string AppendRequestId(string message, string? requestId) =>
        string.IsNullOrWhiteSpace(requestId) ? message : $"{message} request_id={requestId}";

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

public sealed record DoubaoSearchArgs
{
    [ToolParam("Search query, 1-100 characters.")]
    public required string Query { get; init; }

    [ToolParam("Number of results to return, 1-20. Default: 10.")]
    [JsonPropertyName("doc_count")]
    public int? DocCount { get; init; }

    [ToolParam("Maximum tokens in each text snippet, 1-3000. Default: 500.")]
    [JsonPropertyName("max_snippet_length")]
    public int? MaxSnippetLength { get; init; }

    [ToolParam("Maximum images returned per result, 1-10. Default: 3.")]
    [JsonPropertyName("max_image_count_per_doc")]
    public int? MaxImageCountPerDoc { get; init; }
}

internal sealed record DoubaoSearchProvidersConfig
{
    [JsonPropertyName("doubao_search")]
    public DoubaoSearchProviderConfig? DoubaoSearch { get; init; }
}

internal sealed record DoubaoSearchProviderConfig
{
    public bool Enabled { get; init; } = true;
    public string? ApiKey { get; init; }
    public int? TimeoutSeconds { get; init; } = 30;
}

internal sealed record DoubaoSearchProviderConfigLoadResult(
    bool Success,
    DoubaoSearchProviderConfig? Config,
    string? Error)
{
    public static DoubaoSearchProviderConfigLoadResult Ok(DoubaoSearchProviderConfig config) =>
        new(true, config, null);

    public static DoubaoSearchProviderConfigLoadResult Fail(string error) =>
        new(false, null, error);
}

internal sealed record DoubaoSearchApiRequest
{
    [JsonPropertyName("Query")]
    public required string Query { get; init; }

    [JsonPropertyName("DocCount")]
    public required int DocCount { get; init; }

    [JsonPropertyName("MaxSnippetLength")]
    public required int MaxSnippetLength { get; init; }

    [JsonPropertyName("MaxImageCountPerDoc")]
    public required int MaxImageCountPerDoc { get; init; }
}

internal sealed record DoubaoSearchApiResponse
{
    [JsonPropertyName("ResponseMetadata")]
    public DoubaoSearchResponseMetadata? ResponseMetadata { get; init; }

    [JsonPropertyName("Result")]
    public DoubaoSearchResult? Result { get; init; }
}

internal sealed record DoubaoSearchResponseMetadata
{
    [JsonPropertyName("RequestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("Error")]
    public DoubaoSearchMetadataError? Error { get; init; }
}

internal sealed record DoubaoSearchMetadataError
{
    [JsonPropertyName("CodeN")]
    public long? CodeN { get; init; }

    [JsonPropertyName("Code")]
    public string? Code { get; init; }

    [JsonPropertyName("Message")]
    public string? Message { get; init; }
}

internal sealed record DoubaoSearchResult
{
    [JsonPropertyName("TotalDocCount")]
    public long TotalDocCount { get; init; }

    [JsonPropertyName("Documents")]
    public IReadOnlyList<DoubaoSearchDocument>? Documents { get; init; }

    [JsonPropertyName("ErrorCode")]
    public long ErrorCode { get; init; }

    [JsonPropertyName("ErrorMsg")]
    public string? ErrorMsg { get; init; }
}

internal sealed record DoubaoSearchDocument
{
    [JsonPropertyName("Rank")]
    public long Rank { get; init; }

    [JsonPropertyName("Url")]
    public string? Url { get; init; }

    [JsonPropertyName("Title")]
    public string? Title { get; init; }

    [JsonPropertyName("Snippet")]
    public IReadOnlyList<DoubaoSearchSnippet>? Snippet { get; init; }

    [JsonPropertyName("DocumentInfo")]
    public DoubaoSearchDocumentInfo? DocumentInfo { get; init; }

    [JsonPropertyName("HostInfo")]
    public DoubaoSearchHostInfo? HostInfo { get; init; }
}

internal sealed record DoubaoSearchSnippet
{
    [JsonPropertyName("Type")]
    public string? Type { get; init; }

    [JsonPropertyName("Text")]
    public string? Text { get; init; }

    [JsonPropertyName("Image")]
    public DoubaoSearchSnippetImage? Image { get; init; }
}

internal sealed record DoubaoSearchSnippetImage
{
    [JsonPropertyName("Width")]
    public long? Width { get; init; }

    [JsonPropertyName("Height")]
    public long? Height { get; init; }

    [JsonPropertyName("ImageUrl")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("Alt")]
    public string? Alt { get; init; }
}

internal sealed record DoubaoSearchDocumentInfo
{
    [JsonPropertyName("ContentCharCount")]
    public long? ContentCharCount { get; init; }

    [JsonPropertyName("ContentTokenCount")]
    public long? ContentTokenCount { get; init; }

    [JsonPropertyName("Filetype")]
    public string? Filetype { get; init; }

    [JsonPropertyName("PublishTime")]
    public string? PublishTime { get; init; }
}

internal sealed record DoubaoSearchHostInfo
{
    [JsonPropertyName("Hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("IconUrl")]
    public string? IconUrl { get; init; }
}
