using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.SubAgents;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 记忆图书馆检索工具：供主 Agent 主动检索用户历史记忆（档案、偏好、摘要、计划）。
/// </summary>
[Tool(
    id: "search_memory",
    name: "search_memory",
    description: "搜索用户的记忆图书馆，包括个人档案、偏好、对话摘要等。当需要回忆用户之前说过的话时使用此工具。",
    category: ToolCategory.Memory,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class MemoryLibraryTool : PuddingToolBase<SearchMemoryArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IMemoryLibraryConvenience _libraryConvenience;
    private readonly IMemoryLibrary _memoryLibrary;
    private readonly MemoryExplorerSubAgent _memoryExplorerSubAgent;
    private readonly ILogger<MemoryLibraryTool> _logger;

    public MemoryLibraryTool(
        IMemoryLibraryConvenience libraryConvenience,
        IMemoryLibrary memoryLibrary,
        MemoryExplorerSubAgent memoryExplorerSubAgent,
        ILogger<MemoryLibraryTool> logger)
    {
        _libraryConvenience = libraryConvenience;
        _memoryLibrary = memoryLibrary;
        _memoryExplorerSubAgent = memoryExplorerSubAgent;
        _logger = logger;
    }

    /// <summary>
    /// Tool 执行入口：从工具参数中读取 query/book 参数。
    /// </summary>
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SearchMemoryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query;
        var book = args.Book;

        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail("query is required.");

        var response = await ExecuteCoreAsync(
            query,
            book,
            context.WorkspaceId,
            Array.Empty<string>(),
            ct);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(response, JsonOptions));
    }

    private async Task<SearchMemoryResponse> ExecuteCoreAsync(
        string query,
        string? book,
        string? workspaceId,
        IReadOnlyList<string> recentMessages,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "[MemoryLibraryTool] Search start workspace={Workspace} book={Book} query={Query}",
            string.IsNullOrWhiteSpace(workspaceId) ? "-" : workspaceId,
            string.IsNullOrWhiteSpace(book) ? "-" : book,
            query);

        var results = string.IsNullOrWhiteSpace(workspaceId)
            ? (await _libraryConvenience.SmartSearchAsync(query, topK: 8, ct)).ToList()
            : (await _memoryLibrary.SearchChaptersFtsScopedAsync(workspaceId, query, topK: 8, ct)).ToList();

        // Fallback: if scoped search returns nothing, try convenience layer.
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(workspaceId))
            results = (await _libraryConvenience.SmartSearchAsync(query, topK: 8, ct)).ToList();

        if (!string.IsNullOrWhiteSpace(book))
        {
            results = results
                .Where(r => string.Equals(r.BookTitle, book, StringComparison.OrdinalIgnoreCase)
                         || r.BookTitle.Contains(book, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var mappedResults = results
            .OrderByDescending(r => r.Score)
            .Take(5)
            .Select(r => new SearchMemoryItem(
                r.BookTitle,
                r.ChapterTitle,
                r.Snippet,
                r.Score,
                r.MatchSource))
            .ToList();

        string? explorationSummary = null;
        var insufficient = mappedResults.Count < 2 || (mappedResults.Count > 0 && mappedResults[0].Score < 0.2);
        if (insufficient && !string.IsNullOrWhiteSpace(workspaceId))
        {
            _logger.LogDebug(
                "[MemoryLibraryTool] Results insufficient, trigger memory explorer workspace={Workspace} query={Query}",
                workspaceId,
                query);

            explorationSummary = await _memoryExplorerSubAgent.ExploreAsync(
                query,
                workspaceId,
                recentMessages.ToArray(),
                ct);
        }

        return new SearchMemoryResponse(
            Query: query,
            Book: book,
            Results: mappedResults,
            ExplorationSummary: explorationSummary);
    }

    private sealed record SearchMemoryResponse(
        string Query,
        string? Book,
        IReadOnlyList<SearchMemoryItem> Results,
        string? ExplorationSummary);

    private sealed record SearchMemoryItem(
        string Book,
        string? Chapter,
        string Snippet,
        double Score,
        string Source);
}

public sealed record SearchMemoryArgs
{
    [ToolParam("Search keywords or question for memory retrieval.")]
    public required string? Query { get; init; }

    [ToolParam("Optional book hint such as 用户档案 or 用户偏好.")]
    public string? Book { get; init; }
}
