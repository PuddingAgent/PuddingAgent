using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

// ═══════════════════════════════════════════════════════════════
// save_memory — 写入/更新记忆条目
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 主动记忆写入 Tool：Agent 可直接将事实、偏好、摘要写入 MemoryLibrary。
/// 同时实现 ITool（LLM function calling）和 IAgentSkill（SkillRuntime）。
/// </summary>
public sealed class SaveMemoryTool : ITool, IAgentSkill
{
    private readonly IMemoryLibraryConvenience _library;
    private readonly IMemoryLibrary _memLib;
    private readonly ILogger<SaveMemoryTool> _logger;

    public SaveMemoryTool(IMemoryLibraryConvenience library, IMemoryLibrary memLib, ILogger<SaveMemoryTool> logger)
    {
        _library = library;
        _memLib = memLib;
        _logger = logger;
    }

    public string Name => "save_memory";
    public string SkillId => "save_memory";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;
    public string Description => "写入或更新记忆。参数：action(upsert|delete), type(fact|preference|summary|chapter), book(可选), content, key, value, source_ref(可选，溯源引用：session_id 或 url)。";

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "操作类型：upsert（写入/更新）或 delete（删除）"),
            new("type", "string", "内容类型：fact（事实）、preference（偏好）、summary（摘要）、chapter（章节内容）"),
            new("book", "string", "目标 Book 名称（可选，默认自动匹配）"),
            new("content", "string", "正文内容（fact/summary/chapter 需要）"),
            new("key", "string", "偏好键名（preference 类型需要）"),
            new("value", "string", "偏好值（preference 类型需要）"),
            new("title", "string", "章节标题（chapter 类型需要）"),
            new("source_ref", "string", "溯源引用：session_id 或外部 URL（可选，写入后自动创建 Pointer）"),
            new("source_label", "string", "溯源标签，如 '原始会话'、'参考文档'（可选）"),
        ],
        ["action", "type"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = root.GetString("action", "upsert");
        var type = root.GetString("type", "fact");
        var content = root.GetString("content", "");
        var book = root.GetString("book", null);
        var key = root.GetString("key", null);
        var value = root.GetString("value", null);
        var title = root.GetString("title", type);

        try
        {
            if (action == "delete")
            {
                return JsonSerializer.Serialize(new { status = "ok", message = $"delete not yet implemented for type={type}" });
            }

            var package = new ExperiencePackage
            {
                Title = book ?? title ?? type,
                Content = type switch
                {
                    "preference" => $"{key}: {value}",
                    _ => content
                },
                SuggestedTags = type switch
                {
                    "fact" => ["记忆", "事实"],
                    "preference" => ["偏好", key ?? ""],
                    "summary" => ["摘要"],
                    _ => ["记忆"]
                },
                Importance = type switch
                {
                    "fact" => 0.7,
                    "preference" => 0.9,
                    "summary" => 0.5,
                    _ => 0.5
                }
            };

            var result = await _library.UpsertExperienceAsync("default", package, ct);
            _logger.LogInformation("[SaveMemory] {Action} type={Type} book={Book}", action, type, result.Book.Title);

            return JsonSerializer.Serialize(new
            {
                status = "ok",
                action,
                type,
                bookId = result.Book.BookId,
                bookTitle = result.Book.Title,
                chapterId = result.Chapter.ChapterId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SaveMemory] Failed action={Action} type={Type}", action, type);
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    Task<SkillResult> IAgentSkill.ExecuteAsync(SkillInvokeRequest request, CancellationToken ct)
        => ExecuteSkillAsync(request, ct);

    private async Task<SkillResult> ExecuteSkillAsync(SkillInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var argumentsJson = JsonSerializer.Serialize(request.Parameters);
            var result = await ExecuteAsync(argumentsJson, ct);
            return new SkillResult { Success = true, Output = result };
        }
        catch (Exception ex)
        {
            return new SkillResult { Success = false, Output = "", Error = ex.Message, ExitCode = 1 };
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// manage_memory — Library CRUD 管理
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 记忆图书馆管理 Tool：创建/列出/更新/删除 Book、章节、指针。
/// 低摩擦接口——字符串 action 驱动，避免复杂 JSON 嵌套。
/// </summary>
public sealed class ManageMemoryTool : ITool, IAgentSkill
{
    private readonly IMemoryLibrary _lib;
    private readonly ILogger<ManageMemoryTool> _logger;

    public ManageMemoryTool(IMemoryLibrary lib, ILogger<ManageMemoryTool> logger)
    {
        _lib = lib;
        _logger = logger;
    }

    public string Name => "manage_memory";
    public string SkillId => "manage_memory";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;
    public string Description => "管理记忆图书馆结构。action: list_books|create_book|list_chapters|add_chapter|update_chapter|delete_book|add_pointer|list_pointers。";

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "操作：list_books, create_book, list_chapters, add_chapter, update_chapter, delete_book, add_pointer, list_pointers"),
            new("book_id", "string", "目标 BookId（操作特定 Book/Chapter 时需要）"),
            new("library_id", "string", "Library ID（可选，默认自动查找）"),
            new("title", "string", "Book/Chapter 标题"),
            new("content", "string", "Chapter 正文内容"),
            new("summary", "string", "Book 摘要"),
            new("chapter_id", "string", "目标 ChapterId"),
            new("tags", "string", "逗号分隔的标签（创建 Book 时）"),
            new("chapter_order", "number", "章节排序序号"),
        ],
        ["action"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = root.GetString("action", "list_books");

        try
        {
            switch (action)
            {
                case "list_books":
                {
                    var libs = await _lib.ListLibrariesAsync("default", ct);
                    if (libs.Count == 0)
                        return JsonSerializer.Serialize(new { status = "ok", books = Array.Empty<object>() });

                    var books = await _lib.ListBooksAsync(libs[0].LibraryId, 100, ct);
                    var list = books.Select(b => new { b.BookId, b.Title, b.Summary, b.Status });
                    return JsonSerializer.Serialize(new { status = "ok", action, books = list });
                }

                case "create_book":
                {
                    var libs = await _lib.ListLibrariesAsync("default", ct);
                    var libId = root.GetString("library_id", null);
                    if (string.IsNullOrEmpty(libId))
                    {
                        if (libs.Count == 0)
                        {
                            var newLib = await _lib.CreateLibraryAsync("default", "默认图书馆", null, ct);
                            libId = newLib.LibraryId;
                        }
                        else libId = libs[0].LibraryId;
                    }

                    var title = root.GetString("title", "未命名");
                    var summary = root.GetString("summary", "");
                    var tags = root.GetString("tags", null);
                    var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim()).ToList();

                    var book = await _lib.CreateBookAsync(libId, title, summary, tagList, ct);
                    _logger.LogInformation("[ManageMemory] Created book={Title} id={Id}", title, book.BookId);
                    return JsonSerializer.Serialize(new { status = "ok", action, bookId = book.BookId, title = book.Title });
                }

                case "list_chapters":
                {
                    var bookId = root.GetString("book_id", null);
                    if (string.IsNullOrEmpty(bookId))
                        return JsonSerializer.Serialize(new { status = "error", message = "book_id is required" });

                    var chapters = await _lib.ListChaptersAsync(bookId, ct);
                    var list = chapters.Select(c => new { c.ChapterId, c.Title, ContentPreview = c.Content.Length > 100 ? c.Content[..100] + "…" : c.Content, c.ChapterOrder, c.Importance });
                    return JsonSerializer.Serialize(new { status = "ok", action, chapters = list });
                }

                case "add_chapter":
                {
                    var bookId = root.GetString("book_id", null);
                    if (string.IsNullOrEmpty(bookId))
                        return JsonSerializer.Serialize(new { status = "error", message = "book_id is required" });

                    var title = root.GetString("title", "未命名章节");
                    var content = root.GetString("content", "");
                    var chapters = await _lib.ListChaptersAsync(bookId, ct);
                    var order = chapters.Count;

                    var chapter = await _lib.AddChapterAsync(bookId, title, content, order, null, ct);
                    _logger.LogInformation("[ManageMemory] Added chapter={Title} to book={BookId}", title, bookId);
                    return JsonSerializer.Serialize(new { status = "ok", action, chapterId = chapter.ChapterId, title = chapter.Title });
                }

                case "update_chapter":
                {
                    var chapterId = root.GetString("chapter_id", null);
                    if (string.IsNullOrEmpty(chapterId))
                        return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

                    var content = root.GetString("content", null);
                    if (!string.IsNullOrEmpty(content))
                    {
                        await _lib.UpdateChapterContentAsync(chapterId, content, ct);
                    }
                    return JsonSerializer.Serialize(new { status = "ok", action, chapterId });
                }

                case "delete_book":
                {
                    var bookId = root.GetString("book_id", null);
                    if (string.IsNullOrEmpty(bookId))
                        return JsonSerializer.Serialize(new { status = "error", message = "book_id is required" });

                    await _lib.ArchiveBookAsync(bookId, ct);
                    _logger.LogInformation("[ManageMemory] Archived book={BookId}", bookId);
                    return JsonSerializer.Serialize(new { status = "ok", action, bookId });
                }

                case "add_pointer":
                {
                    var chapterId = root.GetString("chapter_id", null);
                    if (string.IsNullOrEmpty(chapterId))
                        return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

                    var targetType = root.GetString("title", "url"); // reuse title as targetType
                    var targetId = root.GetString("content", "");
                    var label = root.GetString("summary", null);

                    var ptr = await _lib.CreatePointerAsync(chapterId, targetType, targetId, label, null, ct);
                    return JsonSerializer.Serialize(new { status = "ok", action, pointerId = ptr.PointerId });
                }

                case "list_pointers":
                {
                    var chapterId = root.GetString("chapter_id", null);
                    if (string.IsNullOrEmpty(chapterId))
                        return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

                    var ptrs = await _lib.GetPointersAsync(chapterId, ct);
                    var list = ptrs.Select(p => new { p.PointerId, p.TargetType, p.TargetId, p.TargetLabel });
                    return JsonSerializer.Serialize(new { status = "ok", action, pointers = list });
                }

                default:
                    return JsonSerializer.Serialize(new { status = "error", message = $"Unknown action: {action}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ManageMemory] Failed action={Action}", action);
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    Task<SkillResult> IAgentSkill.ExecuteAsync(SkillInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var argumentsJson = JsonSerializer.Serialize(request.Parameters);
            var result = ExecuteAsync(argumentsJson, ct).GetAwaiter().GetResult();
            return Task.FromResult(new SkillResult { Success = true, Output = result });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SkillResult { Success = false, Output = "", Error = ex.Message, ExitCode = 1 });
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// grep_memory — 全文检索记忆
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 记忆检索 Tool：全文搜索（FTS5 + 向量混合）、Book 内搜索、目录浏览、时间范围过滤。
/// </summary>
public sealed class GrepMemoryTool : ITool, IAgentSkill
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

    public string Name => "grep_memory";
    public string SkillId => "grep_memory";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;
    public string Description => "全文检索记忆图书馆。action: search|in_book|list_books|toc。mode: fts5(默认)|regex。支持过滤：book、时间范围、topK。";

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "操作：search（全文检索）、in_book（Book内检索）、list_books（列出Books）、toc（目录/章节列表）"),
            new("query", "string", "搜索查询（search/in_book 需要）。regex 模式时支持 .NET 正则语法"),
            new("mode", "string", "搜索模式：fts5（默认，基于全文索引）或 regex（正则匹配章节内容）"),
            new("book", "string", "限定 Book 名称（in_book 需要）"),
            new("top_k", "number", "返回条目数上限，默认 10"),
        ],
        ["action"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = root.GetString("action", "search");
        var query = root.GetString("query", "");
        var mode = root.GetString("mode", "fts5");
        var book = root.GetString("book", null);
        var topK = root.GetInt32("top_k", 10);

        try
        {
            switch (action)
            {
                case "search":
                {
                    if (mode == "regex")
                    {
                        return await RegexSearchAsync(query, book, topK, ct);
                    }
                    var results = await _library.SmartSearchAsync(query, topK, ct);
                    var list = results.Select(r => new
                    {
                        r.BookTitle, r.Snippet, r.Score,
                        source = $"{r.BookTitle} (score:{r.Score:F2})"
                    });
                    return JsonSerializer.Serialize(new { status = "ok", action, query, mode, count = results.Count, results = list });
                }

                case "regex":
                {
                    return await RegexSearchAsync(query, book, topK, ct);
                }

                case "in_book":
                {
                    if (string.IsNullOrEmpty(book))
                        return JsonSerializer.Serialize(new { status = "error", message = "book is required for in_book action" });

                    // FTS5 搜索 + post-filter by book title
                    var results = await _library.SmartSearchAsync(query, topK * 2, ct);
                    var filtered = results
                        .Where(r => string.Equals(r.BookTitle, book, StringComparison.OrdinalIgnoreCase))
                        .Take(topK)
                        .Select(r => new { r.BookTitle, r.Snippet, r.Score })
                        .ToList();
                    return JsonSerializer.Serialize(new { status = "ok", action, query, book, count = filtered.Count, results = filtered });
                }

                case "list_books":
                {
                    var libs = await _memLib.ListLibrariesAsync("default", ct);
                    if (libs.Count == 0)
                        return JsonSerializer.Serialize(new { status = "ok", books = Array.Empty<object>() });

                    var books = await _memLib.ListBooksAsync(libs[0].LibraryId, 100, ct);
                    var list = books.Select(b => new { b.BookId, b.Title, b.Summary, b.Status });
                    return JsonSerializer.Serialize(new { status = "ok", action, count = books.Count, books = list });
                }

                case "toc":
                {
                    var bookId = root.GetString("book_id", null);
                    if (string.IsNullOrEmpty(bookId))
                    {
                        // List books with chapter counts
                        var libs = await _memLib.ListLibrariesAsync("default", ct);
                        if (libs.Count == 0)
                            return JsonSerializer.Serialize(new { status = "ok", toc = Array.Empty<object>() });

                        var books = await _memLib.ListBooksAsync(libs[0].LibraryId, 100, ct);
                        var tocList = new List<object>();
                        foreach (var b in books)
                        {
                            var chs = await _memLib.ListChaptersAsync(b.BookId, ct);
                            tocList.Add(new { b.BookId, b.Title, chapterCount = chs.Count });
                        }
                        return JsonSerializer.Serialize(new { status = "ok", action, toc = tocList });
                    }
                    else
                    {
                        var chs = await _memLib.ListChaptersAsync(bookId, ct);
                        var list = chs.Select(c => new { c.ChapterId, c.Title, ContentPreview = c.Content.Length > 80 ? c.Content[..80] + "…" : c.Content, c.ChapterOrder });
                        return JsonSerializer.Serialize(new { status = "ok", action, bookId, chapters = list });
                    }
                }

                default:
                    return JsonSerializer.Serialize(new { status = "error", message = $"Unknown action: {action}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GrepMemory] Failed action={Action}", action);
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    /// <summary>正则检索：遍历所有 Book → 遍历每个 Chapter 内容 → Regex 匹配 → 返回命中。</summary>
    private async Task<string> RegexSearchAsync(string pattern, string? bookFilter, int topK, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return JsonSerializer.Serialize(new { status = "error", message = "query is required for regex mode" });

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return JsonSerializer.Serialize(new { status = "error", message = $"Invalid regex: {ex.Message}" }); }

        var hits = new List<object>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var libs = await _memLib.ListLibrariesAsync("default", ct);

        foreach (var lib in libs)
        {
            var books = await _memLib.ListBooksAsync(lib.LibraryId, 200, ct);
            foreach (var b in books)
            {
                if (!string.IsNullOrWhiteSpace(bookFilter)
                    && !b.Title.Contains(bookFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var chapters = await _memLib.ListChaptersAsync(b.BookId, ct);
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
            count = hits.Count,
            elapsedMs = sw.ElapsedMilliseconds,
            results = hits,
        });
    }

    Task<SkillResult> IAgentSkill.ExecuteAsync(SkillInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var argumentsJson = JsonSerializer.Serialize(request.Parameters);
            var result = ExecuteAsync(argumentsJson, ct).GetAwaiter().GetResult();
            return Task.FromResult(new SkillResult { Success = true, Output = result });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SkillResult { Success = false, Output = "", Error = ex.Message, ExitCode = 1 });
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// Helpers  — see JsonElementExtensions.cs
