// ══════════════════════════════════════════════════════════════════════════════════
// grep_memory — 全文检索记忆
// ══════════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 记忆检索 Tool：全文搜索（FTS5 + 向量混合）、Book 内搜索、目录浏览、时间范围过滤。
/// </summary>
[Tool(
    id: "grep_memory",
    name: "grep_memory",
    description: "全文检索记忆图书馆。action: search|in_book|list_books|toc。mode: fts5(默认)|regex。支持过滤：book、时间范围、topK。",
    category: ToolCategory.Memory,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class GrepMemoryTool : PuddingToolBase<GrepMemoryArgs>
{
    private readonly IMemoryLibraryConvenience _library;
    private readonly IMemoryLibrary _memLib;
    private readonly ILogger<GrepMemoryTool> _logger;

    public GrepMemoryTool(IMemoryLibraryConvenience library, IMemoryLibrary memLib, ILogger<GrepMemoryTool> logger)
    {
        _library = library;
        _memLib = memLib;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GrepMemoryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var root = BuildRoot(args, context);
        var action = root.GetString("action", "search");
        var query = root.GetString("query", "");
        var mode = root.GetString("mode", "fts5");
        var book = root.GetOptionalString("book");
        var topK = root.GetInt32("top_k", 10);
        var includeHistory = root.GetBoolean("include_history", false);
        var workspaceId = context.WorkspaceId;
        var agentInstanceId = context.AgentInstanceId;

        try
        {
            string result;
            switch (action)
            {
                case "search":
                {
                    if (mode == "regex")
                    {
                        result = await RegexSearchAsync(query, book, topK, workspaceId, includeHistory, ct);
                        break;
                    }
                    // ADR-029: 仅使用 scoped 搜索，不回退到 unscoped path
                    var results = await _memLib.SearchChaptersFtsScopedAsync(workspaceId, query, topK, ct, agentInstanceId, includeHistory);
                    if (results.Count == 0)
                    {
                        _logger.LogDebug("[GrepMemory] no scoped hits workspace={WorkspaceId} query={Query}", workspaceId, query);
                        results = await ScopedSubstringSearchAsync(query, book, topK, workspaceId, agentInstanceId, includeHistory, ct);
                    }
                    var list = results.Select(r => new
                    {
                        r.BookTitle, r.ChapterTitle, r.Snippet, r.Score, r.MatchSource,
                        r.Status, r.SupersededByChapterId, r.SupersededAt,
                        source = $"{r.BookTitle} (score:{r.Score:F2})"
                    });
                    result = JsonSerializer.Serialize(new { status = "ok", action, query, mode, workspaceId, includeHistory, count = results.Count, results = list });
                    break;
                }

                case "regex":
                {
                    result = await RegexSearchAsync(query, book, topK, workspaceId, includeHistory, ct);
                    break;
                }

                case "in_book":
                {
                    if (string.IsNullOrEmpty(book))
                    {
                        result = JsonSerializer.Serialize(new { status = "error", message = "book is required for in_book action" });
                        break;
                    }

                    // scoped FTS search + post-filter by book title
                    var results = await _memLib.SearchChaptersFtsScopedAsync(workspaceId, query, topK * 2, ct, agentInstanceId, includeHistory);
                    var filtered = results
                        .Where(r => string.Equals(r.BookTitle, book, StringComparison.OrdinalIgnoreCase))
                        .Take(topK)
                        .Select(r => new { r.BookTitle, r.Snippet, r.Score, r.Status, r.SupersededByChapterId, r.SupersededAt })
                        .ToList();
                    result = JsonSerializer.Serialize(new { status = "ok", action, query, book, workspaceId, includeHistory, count = filtered.Count, results = filtered });
                    break;
                }

                case "list_books":
                {
                    var books = await _memLib.ListBooksScopedAsync(workspaceId, 100, ct);
                    var list = books.Select(b => new { b.BookId, b.Title, b.Summary, b.Status });
                    result = JsonSerializer.Serialize(new { status = "ok", action, workspaceId, count = books.Count, books = list });
                    break;
                }

                case "toc":
                {
                    var bookId = root.GetOptionalString("book_id");
                    if (string.IsNullOrEmpty(bookId))
                    {
                        // List books with chapter counts, scoped by workspace
                        var books = await _memLib.ListBooksScopedAsync(workspaceId, 100, ct);
                        var tocList = new List<object>();
                        foreach (var b in books)
                        {
                            var chs = includeHistory
                                ? await _memLib.ListChapterHistoryAsync(b.BookId, ct)
                                : await _memLib.ListChaptersAsync(b.BookId, ct);
                            tocList.Add(new { b.BookId, b.Title, chapterCount = chs.Count });
                        }
                        result = JsonSerializer.Serialize(new { status = "ok", action, workspaceId, includeHistory, toc = tocList });
                        break;
                    }
                    else
                    {
                        var chs = includeHistory
                            ? await _memLib.ListChapterHistoryAsync(bookId, ct)
                            : await _memLib.ListChaptersAsync(bookId, ct);
                        var list = chs.Select(c => new
                        {
                            c.ChapterId, c.Title,
                            ContentPreview = c.Content.Length > 80 ? c.Content[..80] + "…" : c.Content,
                            c.ChapterOrder, c.Status, c.SupersededByChapterId, c.SupersededAt
                        });
                        result = JsonSerializer.Serialize(new { status = "ok", action, bookId, includeHistory, chapters = list });
                        break;
                    }
                }

                default:
                    result = JsonSerializer.Serialize(new { status = "error", message = $"Unknown action: {action}" });
                    break;
            }

            return ToolExecutionResult.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GrepMemory] Failed action={Action}", action);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { status = "error", message = ex.Message }));
        }
    }

    /// <summary>正则检索：遍历所有 Book → 遍历每个 Chapter 内容 → Regex 匹配 → 返回命中。</summary>
    private async Task<string> RegexSearchAsync(string pattern, string? bookFilter, int topK, string workspaceId, bool includeHistory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return JsonSerializer.Serialize(new { status = "error", message = "query is required for regex mode" });

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return JsonSerializer.Serialize(new { status = "error", message = $"Invalid regex: {ex.Message}" }); }

        var hits = new List<object>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var books = await _memLib.ListBooksScopedAsync(workspaceId, 200, ct);

        foreach (var b in books)
        {
            if (!string.IsNullOrWhiteSpace(bookFilter)
                && !b.Title.Contains(bookFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var chapters = includeHistory
                ? await _memLib.ListChapterHistoryAsync(b.BookId, ct)
                : await _memLib.ListChaptersAsync(b.BookId, ct);
            foreach (var ch in chapters)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(ch.Content)) continue;

                var matches = regex.Matches(ch.Content);
                if (matches.Count == 0) continue;

                // 取每个匹配周围的上下文（前后 40 字符）
                foreach (Match m in matches)
                {
                    var start = Math.Max(0, m.Index - 40);
                    var len = Math.Min(ch.Content.Length - start, m.Length + 80);
                    var snippet = ch.Content.Substring(start, len);
                    if (start > 0) snippet = "…" + snippet;
                    if (start + len < ch.Content.Length) snippet += "…";

                    hits.Add(new
                    {
                        book = b.Title,
                        bookId = b.BookId,
                        chapter = ch.Title,
                        chapterId = ch.ChapterId,
                        ch.Status,
                        ch.SupersededByChapterId,
                        ch.SupersededAt,
                        match = m.Value,
                        snippet,
                        position = m.Index,
                    });

                    if (hits.Count >= topK) break;
                }
                if (hits.Count >= topK) break;
            }
            if (hits.Count >= topK) break;
        }

        sw.Stop();
        _logger.LogInformation("[GrepMemory] regex pattern={Pattern} book={Book} hits={Hits} elapsed={Ms}ms",
            pattern, bookFilter ?? "*", hits.Count, sw.ElapsedMilliseconds);

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "search",
            mode = "regex",
            query = pattern,
            book = bookFilter,
            includeHistory,
            count = hits.Count,
            elapsedMs = sw.ElapsedMilliseconds,
            results = hits,
        });
    }

    private async Task<IReadOnlyList<RankedResult>> ScopedSubstringSearchAsync(
        string query,
        string? bookFilter,
        int topK,
        string workspaceId,
        string? agentInstanceId,
        bool includeHistory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<RankedResult>();

        var results = new List<RankedResult>();
        // Note: ListBooksScopedAsync doesn't filter by AgentInstanceId (books are workspace-level),
        // but individual chapters carry AgentInstanceId for post-filtering.
        var books = await _memLib.ListBooksScopedAsync(workspaceId, 200, ct);
        foreach (var b in books)
        {
            if (!string.IsNullOrWhiteSpace(bookFilter)
                && !b.Title.Contains(bookFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var chapters = includeHistory
                ? await _memLib.ListChapterHistoryAsync(b.BookId, ct)
                : await _memLib.ListChaptersAsync(b.BookId, ct);
            foreach (var ch in chapters)
            {
                // ADR-042: 过滤 Agent 私有章节（仅返回该 Agent 的 + 共享章节）
                if (!string.IsNullOrWhiteSpace(agentInstanceId)
                    && !string.IsNullOrWhiteSpace(ch.AgentInstanceId)
                    && !string.Equals(ch.AgentInstanceId, agentInstanceId, StringComparison.Ordinal))
                    continue;
                var index = ch.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;

                var start = Math.Max(0, index - 60);
                var length = Math.Min(ch.Content.Length - start, query.Length + 120);
                var snippet = ch.Content.Substring(start, length);
                if (start > 0) snippet = "…" + snippet;
                if (start + length < ch.Content.Length) snippet += "…";
                results.Add(new RankedResult
                {
                    BookId = b.BookId,
                    BookTitle = b.Title,
                    ChapterId = ch.ChapterId,
                    ChapterTitle = ch.Title,
                    Snippet = snippet,
                    Score = 0.1,
                    MatchSource = "like",
                    Status = ch.Status,
                    SupersededByChapterId = ch.SupersededByChapterId,
                    SupersededAt = ch.SupersededAt
                });

                if (results.Count >= topK)
                    return results;
            }
        }

        return results;
    }

    private static JsonElement BuildRoot(GrepMemoryArgs args, ToolExecutionContext context)
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = args.Action,
            ["query"] = args.Query,
            ["mode"] = args.Mode,
            ["book"] = args.Book,
            ["top_k"] = args.TopK,
            ["book_id"] = args.BookId,
            ["include_history"] = args.IncludeHistory,
        });
    }
}
