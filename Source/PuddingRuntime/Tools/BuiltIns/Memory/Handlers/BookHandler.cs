using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools.Handlers;

/// <summary>
/// 记忆工具 Handler：Book 相关操作（create / list / delete）。
/// </summary>
public sealed class BookHandler
{
    private readonly IMemoryLibrary _lib;
    private readonly ILogger<BookHandler> _logger;

    public BookHandler(IMemoryLibrary lib, ILogger<BookHandler> logger)
    {
        _lib = lib;
        _logger = logger;
    }

    // ── list_books ──

    public async Task<string> ListBooksAsync(string workspaceId, CancellationToken ct)
    {
        var books = await _lib.ListBooksScopedAsync(workspaceId, 100, ct);
        var list = books.Select(b => new { b.BookId, b.Title, b.Summary, b.Status });
        return JsonSerializer.Serialize(new { status = "ok", action = "list_books", workspaceId, books = list });
    }

    // ── create_book ──

    public async Task<string> CreateBookAsync(JsonElement root, string workspaceId, CancellationToken ct)
    {
        var libs = await _lib.ListLibrariesAsync(workspaceId, ct);
        var libId = root.GetOptionalString("library_id");
        if (string.IsNullOrEmpty(libId))
        {
            if (libs.Count == 0)
            {
                var newLib = await _lib.CreateLibraryAsync(workspaceId, "默认图书馆", null, ct);
                libId = newLib.LibraryId;
            }
            else libId = libs[0].LibraryId;
        }

        var title = root.GetString("title", "未命名");
        var summary = root.GetString("summary", "");
        var tags = root.GetOptionalString("tags");
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).ToList();

        // Phase 2: BookRegistry 路由 — 将别名映射到标准 Book
        var canonicalId = BookRegistry.TryResolveCanonicalId(title);
        if (canonicalId is not null)
        {
            var canonicalTitle = BookRegistry.StandardBooks
                .First(b => b.Id == canonicalId).CanonicalTitle;
            var canonicalBook = await _lib.FindBookByTitleAsync(libId, canonicalTitle, ct);
            if (canonicalBook is not null)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "routed",
                    message = $"✅ 标题 \"{title}\" 已路由到标准 Book \"{canonicalTitle}\"。",
                    bookId = canonicalBook.BookId,
                    title = canonicalBook.Title
                });
            }
            var stdTemplate = BookRegistry.StandardBooks.First(b => b.Id == canonicalId);
            var stdBook = await _lib.CreateBookAsync(
                libId, stdTemplate.CanonicalTitle, stdTemplate.Description, stdTemplate.Tags, ct);
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                action = "create_book",
                message = $"✅ 创建标准 Book \"{stdTemplate.CanonicalTitle}\"（从别名 \"{title}\" 路由）",
                bookId = stdBook.BookId,
                title = stdBook.Title
            });
        }

        // Phase 0: 去重检测
        var existingBook = await _lib.FindBookByTitleAsync(libId, title, ct);
        if (existingBook is not null)
        {
            var existingChapters = await _lib.ListChaptersAsync(existingBook.BookId, ct);
            return JsonSerializer.Serialize(new
            {
                status = "duplicate",
                message = $"❌ Book \"{title}\" 已存在于当前图书馆中，无法重复创建。",
                guidance = "建议操作：",
                suggestions = new[]
                {
                    $"1. 追加内容 → 使用 add_chapter，指定 book_id=\"{existingBook.BookId}\"",
                    $"2. 更新已有章节 → 使用 update_chapter，指定对应 chapter_id",
                    "3. 如果确实需要新建，请使用不同的 title"
                },
                existingBook = new
                {
                    existingBook.BookId,
                    existingBook.Title,
                    existingBook.Summary,
                    ChapterCount = existingChapters.Count
                }
            });
        }

        var book = await _lib.CreateBookAsync(libId, title, summary, tagList, ct);
        _logger.LogInformation("[BookHandler] Created book={Title} id={Id} workspace={WsId}", title, book.BookId, workspaceId);
        return JsonSerializer.Serialize(new { status = "ok", action = "create_book", bookId = book.BookId, title = book.Title });
    }

    // ── delete_book ──

    public async Task<string> DeleteBookAsync(string bookId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bookId))
            return JsonSerializer.Serialize(new { status = "error", message = "book_id is required" });

        var deleted = await _lib.DeleteBookAsync(bookId, ct);
        if (!deleted)
            return JsonSerializer.Serialize(new { status = "error", action = "delete_book", bookId, message = "book not found" });

        _logger.LogInformation("[BookHandler] Deleted book={BookId}", bookId);
        return JsonSerializer.Serialize(new { status = "ok", action = "delete_book", bookId, deleted = true });
    }
}
