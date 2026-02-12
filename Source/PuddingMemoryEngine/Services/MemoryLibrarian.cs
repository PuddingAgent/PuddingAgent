using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 记忆图书馆管理员——ADR-028 Phase 4。
/// 当前阶段包装 IMemoryLibraryConvenience，逐步将智能整理逻辑迁移至此。
/// </summary>
public sealed class MemoryLibrarian : IMemoryLibrarian
{
    private readonly IMemoryLibrary _library;
    private readonly ILogger<MemoryLibrarian> _logger;

    public MemoryLibrarian(
        IMemoryLibrary library,
        ILogger<MemoryLibrarian> logger)
    {
        _library = library;
        _logger = logger;
    }

    public async Task<ExperienceWriteResult> IngestExperienceAsync(
        MemoryIngestionRequest request,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[Librarian] Ingest workspace={Workspace} title={Title}",
            request.WorkspaceId, request.Experience.Title);

        // ADR-029: 直接调用 IMemoryLibrary primitives，不再委托 Convenience
        // Step 1: 确保 Library 存在
        var libs = await _library.ListLibrariesAsync(request.WorkspaceId, ct);
        string libraryId;
        if (libs.Count > 0)
            libraryId = libs[0].LibraryId;
        else
            libraryId = (await _library.CreateLibraryAsync(request.WorkspaceId, "默认图书馆", null, ct)).LibraryId;

        // Step 2: 查找或创建 Book
        var bookTitle = request.TargetBookTitle ?? request.Experience.Title;
        var book = await _library.FindBookByTitleAsync(libraryId, bookTitle, ct);
        if (book is null)
            book = await _library.CreateBookAsync(libraryId, bookTitle,
                request.Experience.Content.Length > 200 ? request.Experience.Content[..200] : request.Experience.Content,
                request.Experience.SuggestedTags, ct);

        // Step 3: 追加 Chapter
        var chapters = await _library.ListChaptersAsync(book.BookId, ct);
        var order = chapters.Count;
        var chapter = await _library.AddChapterWithSourceAsync(
            book.BookId, request.Experience.Title, request.Experience.Content,
            order, request.Experience.SourceSessionId,
            request.Experience.SourceReference, request.Experience.ReferenceType ?? "none", ct);

        // Step 4: 写入 SourceReference
        if (!string.IsNullOrEmpty(request.Experience.SourceReference)
            && !string.IsNullOrEmpty(request.Experience.ReferenceType))
        {
            try
            {
                await _library.AddSourceReferenceAsync(new SourceReferenceCreateRequest(
                    request.WorkspaceId, "chapter", chapter.ChapterId,
                    request.Experience.ReferenceType, request.Experience.SourceReference,
                    Label: request.Experience.Title), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Librarian] Failed to write SourceReference for chapter={ChapterId}",
                    chapter.ChapterId);
            }
        }

        // Step 5: 确保默认 books 存在
        try
        {
            await _library.EnsureDefaultBooksAsync(request.WorkspaceId, libraryId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Librarian] Default books seed skipped for library={LibId}", libraryId);
        }

        return new ExperienceWriteResult(book, chapter);
    }

    public async Task<IReadOnlyList<MemoryTreeOperation>> PlanTreeMaintenanceAsync(
        string workspaceId,
        string libraryId,
        CancellationToken ct = default)
    {
        // Phase 4 scope: return empty, real LLM planning in follow-up
        _logger.LogDebug("[Librarian] PlanTreeMaintenance workspace={Workspace} lib={Lib} — not yet implemented",
            workspaceId, libraryId);
        return Array.Empty<MemoryTreeOperation>();
    }

    public async Task ApplyTreeOperationAsync(
        MemoryTreeOperation operation,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[Librarian] ApplyTreeOperation type={OpType} — delegating to Core",
            operation.OperationType);

        switch (operation.OperationType)
        {
            case MemoryTreeOperationType.CreateNode:
                await _library.CreateTreeNodeAsync(operation.WorkspaceId, operation.LibraryId,
                    operation.ParentNodeId, operation.Name ?? "unnamed", null, "category", ct);
                break;
            case MemoryTreeOperationType.MountBook:
                if (!string.IsNullOrEmpty(operation.BookId) && !string.IsNullOrEmpty(operation.NodeId))
                    await _library.MountBookAsync(operation.BookId, operation.NodeId, operation.Weight, ct);
                break;
            default:
                _logger.LogWarning("[Librarian] ApplyTreeOperation type={OpType} not yet implemented",
                    operation.OperationType);
                break;
        }
    }
}
