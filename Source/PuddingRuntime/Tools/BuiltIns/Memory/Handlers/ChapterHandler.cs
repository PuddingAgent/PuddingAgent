using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools.Handlers;

/// <summary>
/// 记忆工具 Handler：Chapter 相关操作（add / update / delete / list）。
/// </summary>
public sealed class ChapterHandler
{
    private readonly IMemoryLibrary _lib;
    private readonly ILogger<ChapterHandler> _logger;

    public ChapterHandler(IMemoryLibrary lib, ILogger<ChapterHandler> logger)
    {
        _lib = lib;
        _logger = logger;
    }

    // ── list_chapters ──

    public async Task<string> ListChaptersAsync(string bookId, bool includeHistory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bookId))
            return JsonSerializer.Serialize(new { status = "error", message = "book_id is required" });

        var chapters = includeHistory
            ? await _lib.ListChapterHistoryAsync(bookId, ct)
            : await _lib.ListChaptersAsync(bookId, ct);
        var list = chapters.Select(c => new
        {
            c.ChapterId, c.Title,
            ContentPreview = c.Content.Length > 100 ? c.Content[..100] + "…" : c.Content,
            c.ChapterOrder, c.Importance,
            c.Status, c.SupersededByChapterId, c.SupersededAt
        });
        return JsonSerializer.Serialize(new { status = "ok", action = "list_chapters", includeHistory, chapters = list });
    }

    // ── add_chapter ──

    public async Task<string> AddChapterAsync(JsonElement root, string bookId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bookId))
            return JsonSerializer.Serialize(new { status = "error", message = "book_id is required" });

        var title = root.GetString("title", "未命名章节");
        var content = root.GetString("content", "");
        var sourceRef = root.GetOptionalString("source_reference");
        var refType = root.GetOptionalString("reference_type");
        var chapters = await _lib.ListChaptersAsync(bookId, ct);

        // Phase 0: 去重检测
        var existingChapter = chapters.FirstOrDefault(c =>
            string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase));
        if (existingChapter is not null)
        {
            return JsonSerializer.Serialize(new
            {
                status = "duplicate",
                message = $"❌ Chapter \"{title}\" 已存在于 Book (book_id=\"{bookId}\") 中，无法重复添加。",
                guidance = "建议操作：",
                suggestions = new[]
                {
                    $"1. 更新内容 → 使用 update_chapter，指定 chapter_id=\"{existingChapter.ChapterId}\"",
                    "2. 使用不同的 title 添加新章节",
                    "3. 如果内容是追加性质，请使用 update_chapter 将新内容合并到已有章节"
                },
                existingChapter = new
                {
                    existingChapter.ChapterId,
                    existingChapter.Title,
                    ContentPreview = existingChapter.Content.Length > 200
                        ? existingChapter.Content[..200] + "…"
                        : existingChapter.Content
                }
            });
        }

        var scene = root.GetOptionalString("scene");
        var constraints = root.GetOptionalString("constraints");
        var tags = root.GetOptionalString("tags");

        var order = chapters.Count;
        var chapter = await _lib.AddChapterWithSourceAsync(bookId, title, content, order,
            null, sourceRef, refType, ct);

        // Phase 1: 设置元数据字段
        if (scene is not null || constraints is not null || tags is not null)
        {
            await _lib.UpdateChapterMetadataAsync(chapter.ChapterId, scene, constraints, tags, ct);
        }

        _logger.LogInformation("[ChapterHandler] Added chapter={Title} to book={BookId} tags={Tags}", title, bookId, tags ?? "(none)");
        return JsonSerializer.Serialize(new
        {
            status = "ok", action = "add_chapter",
            chapterId = chapter.ChapterId, title = chapter.Title,
            scene, constraints, tags
        });
    }

    // ── update_chapter ──

    public async Task<string> UpdateChapterAsync(JsonElement root, CancellationToken ct)
    {
        var chapterId = root.GetOptionalString("chapter_id");
        if (string.IsNullOrEmpty(chapterId))
            return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

        var title = root.GetOptionalString("title");
        if (!string.IsNullOrEmpty(title))
        {
            await _lib.UpdateChapterTitleAsync(chapterId, title, ct);
        }

        var content = root.GetOptionalString("content");
        if (!string.IsNullOrEmpty(content))
        {
            await _lib.UpdateChapterContentAsync(chapterId, content, ct);
        }

        // Phase 1: 支持更新元数据字段
        var scene = root.GetOptionalString("scene");
        var constraints = root.GetOptionalString("constraints");
        var tags = root.GetOptionalString("tags");
        if (scene is not null || constraints is not null || tags is not null)
        {
            await _lib.UpdateChapterMetadataAsync(chapterId, scene, constraints, tags, ct);
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok", action = "update_chapter",
            chapterId, title, scene, constraints, tags
        });
    }

    // ── delete_chapter ──

    public async Task<string> DeleteChapterAsync(string chapterId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(chapterId))
            return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

        var deleted = await _lib.DeleteChapterAsync(chapterId, ct);
        if (!deleted)
            return JsonSerializer.Serialize(new { status = "error", message = $"Chapter not found: {chapterId}" });

        _logger.LogInformation("[ChapterHandler] Deleted chapter={ChapterId}", chapterId);
        return JsonSerializer.Serialize(new { status = "ok", action = "delete_chapter", chapterId });
    }
}
