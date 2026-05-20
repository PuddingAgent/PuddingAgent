using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.SubAgents;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 记忆图书馆检索工具：供主 Agent 主动检索用户历史记忆（档案、偏好、摘要、计划）。
/// 同时实现 ITool 与 IAgentSkill，兼容两条工具调用链路。
/// </summary>
public sealed class MemoryLibraryTool : ITool, IAgentSkill
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

    /// <summary>工具 ID（LLM function name）。</summary>
    public string Name => "search_memory";

    /// <summary>SkillRuntime 使用的 SkillId。</summary>
    public string SkillId => Name;

    /// <summary>工具说明。</summary>
    public string Description => "搜索用户的记忆图书馆，包括个人档案、偏好、对话摘要等。当需要回忆用户之前说过的话时使用此工具。";

    /// <summary>Skill 是否需要 Shell 权限（本工具不需要）。</summary>
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

    /// <summary>参数定义（JSON schema 简化表示）。</summary>
    public ToolParameterSchema Parameters => new(
        [
            new ToolParameter("query", "string", "要搜索的关键词或问题"),
            new ToolParameter("book", "string", "可选，限定搜索的 Book 名称，如 用户档案、用户偏好"),
            new ToolParameter("workspaceId", "string", "可选，工作区 ID。作为子代理深度探索上下文。"),
        ],
        ["query"]);

    /// <summary>
    /// ITool 执行入口：argumentsJson 包含 query/book/workspaceId。
    /// </summary>
    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        SearchMemoryArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<SearchMemoryArgs>(argumentsJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MemoryLibraryTool] Invalid arguments json.");
            return JsonSerializer.Serialize(new { error = "Invalid arguments JSON." }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(args?.Query))
            return JsonSerializer.Serialize(new { error = "query is required." }, JsonOptions);

        var response = await ExecuteCoreAsync(
            args.Query,
            args.Book,
            args.WorkspaceId,
            args.RecentMessages ?? Array.Empty<string>(),
            ct);

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// IAgentSkill 执行入口：从 SkillInvokeRequest 中读取 query/book 参数。
    /// </summary>
    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var query = request.Parameters.TryGetValue("query", out var q) && !string.IsNullOrWhiteSpace(q)
            ? q
            : request.Input;

        var book = request.Parameters.TryGetValue("book", out var b) ? b : null;

        if (string.IsNullOrWhiteSpace(query))
        {
            return new SkillResult
            {
                Success = false,
                Output = string.Empty,
                Error = "query is required.",
                ExitCode = 1,
            };
        }

        var response = await ExecuteCoreAsync(
            query,
            book,
            request.WorkspaceId,
            Array.Empty<string>(),
            ct);

        return new SkillResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(response, JsonOptions),
            ExitCode = 0,
        };
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

        // Fallback: if scoped search returns nothing, try convenience layer
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

    private sealed record SearchMemoryArgs
    {
        public string Query { get; init; } = string.Empty;
        public string? Book { get; init; }
        public string? WorkspaceId { get; init; }
        public IReadOnlyList<string>? RecentMessages { get; init; }
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
