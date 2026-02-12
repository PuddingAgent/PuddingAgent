using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools.Handlers;

/// <summary>
/// 记忆工具 Handler：Book 去重操作（dedup_report / merge_chapters）。
/// </summary>
public sealed class DedupHandler
{
    private readonly IMemoryLibrary _lib;
    private readonly ILogger<DedupHandler> _logger;

    public DedupHandler(IMemoryLibrary lib, ILogger<DedupHandler> logger)
    {
        _lib = lib;
        _logger = logger;
    }

    // ── dedup_report ──

    public async Task<string> DedupReportAsync(string workspaceId, CancellationToken ct)
    {
        var libs = await _lib.ListLibrariesAsync(workspaceId, ct);
        var report = new List<object>();
        foreach (var lib in libs)
        {
            var books = await _lib.ListBooksAsync(lib.LibraryId, 200, ct);
            var groups = books
                .GroupBy(b => b.Title.Trim().ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var g in groups)
            {
                var bookDetails = new List<object>();
                foreach (var b in g)
                {
                    var chs = await _lib.ListChaptersAsync(b.BookId, ct);
                    bookDetails.Add(new { b.BookId, b.Title, b.Summary, ChapterCount = chs.Count });
                }
                report.Add(new { duplicateTitle = g.Key, count = g.Count(), books = bookDetails });
            }
        }
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "dedup_report",
            duplicateGroupCount = report.Count,
            duplicates = report
        });
    }

    // ── merge_chapters ──

    public async Task<string> MergeChaptersAsync(string sourceBookId, string targetBookId, CancellationToken ct)
    {
        if (sourceBookId == targetBookId)
            return JsonSerializer.Serialize(new { status = "error", message = "source_book_id 和 target_book_id 不能相同。" });

        var movedCount = await _lib.MergeBookChaptersAsync(sourceBookId, targetBookId, ct);
        _logger.LogInformation("[DedupHandler] Merged book {Src} into {Tgt}, moved {Count} chapters",
            sourceBookId, targetBookId, movedCount);
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "merge_chapters",
            message = $"✅ 已将 {movedCount} 个章节从源 Book 合并到目标 Book，并删除源 Book。",
            sourceBookId,
            targetBookId,
            movedChapters = movedCount
        });
    }
}
