using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools.Handlers;

/// <summary>
/// 记忆工具 Handler：Pointer/引用相关操作（add_pointer / list_pointers）。
/// </summary>
public sealed class ReferenceHandler
{
    private readonly IMemoryLibrary _lib;
    private readonly ILogger<ReferenceHandler> _logger;

    public ReferenceHandler(IMemoryLibrary lib, ILogger<ReferenceHandler> logger)
    {
        _lib = lib;
        _logger = logger;
    }

    // ── add_pointer ──

    public async Task<string> AddPointerAsync(JsonElement root, CancellationToken ct)
    {
        var chapterId = root.GetOptionalString("chapter_id");
        if (string.IsNullOrEmpty(chapterId))
            return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

        var targetType = root.GetString("title", "url"); // reuse title as targetType
        var targetId = root.GetString("content", "");
        var label = root.GetOptionalString("summary");

        var ptr = await _lib.CreatePointerAsync(chapterId, targetType, targetId, label, null, ct);
        return JsonSerializer.Serialize(new { status = "ok", action = "add_pointer", pointerId = ptr.PointerId });
    }

    // ── list_pointers ──

    public async Task<string> ListPointersAsync(JsonElement root, string workspaceId, CancellationToken ct)
    {
        var chapterId = root.GetOptionalString("chapter_id");
        var sourceType = root.GetOptionalString("source_type");
        var sourceId = root.GetOptionalString("source_id");

        IReadOnlyList<PointerRecord> ptrs;
        if (!string.IsNullOrEmpty(chapterId))
        {
            ptrs = await _lib.GetPointersAsync(chapterId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(sourceType) && !string.IsNullOrWhiteSpace(sourceId))
        {
            ptrs = await _lib.GetPointersBySourceAsync(workspaceId, sourceType, sourceId, ct);
        }
        else
        {
            ptrs = await ListWorkspacePointersAsync(workspaceId, ct);
        }

        var list = ptrs.Select(p => new
        {
            p.PointerId, p.SourceType, p.SourceId,
            p.ChapterId, p.TargetType, p.TargetId, p.TargetLabel
        });
        return JsonSerializer.Serialize(new
        {
            status = "ok", action = "list_pointers",
            workspaceId, count = ptrs.Count, pointers = list
        });
    }

    /// <summary>遍历 workspace 下所有 Book → Chapter → Pointer。</summary>
    private async Task<IReadOnlyList<PointerRecord>> ListWorkspacePointersAsync(
        string workspaceId, CancellationToken ct)
    {
        var pointers = new Dictionary<string, PointerRecord>(StringComparer.Ordinal);
        var books = await _lib.ListBooksScopedAsync(workspaceId, 500, ct);
        foreach (var book in books)
        {
            var chapters = await _lib.ListChaptersAsync(book.BookId, ct);
            foreach (var chapter in chapters)
            {
                var chapterPointers = await _lib.GetPointersAsync(chapter.ChapterId, ct);
                foreach (var pointer in chapterPointers)
                    pointers[pointer.PointerId] = pointer;
            }
        }

        return pointers.Values
            .OrderByDescending(p => p.Relevance)
            .ThenByDescending(p => p.CreatedAt)
            .ToList();
    }
}
