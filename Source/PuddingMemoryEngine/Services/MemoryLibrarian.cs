using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 记忆图书馆管理员——ADR-028 Phase 4。
/// 当前阶段包装 IMemoryLibraryConvenience，逐步将智能整理逻辑迁移至此。
/// </summary>
public sealed class MemoryLibrarian : IMemoryLibrarian
{
    private readonly IMemoryLibraryConvenience _convenience;
    private readonly IMemoryLibrary _library;
    private readonly ILogger<MemoryLibrarian> _logger;

    public MemoryLibrarian(
        IMemoryLibraryConvenience convenience,
        IMemoryLibrary library,
        ILogger<MemoryLibrarian> logger)
    {
        _convenience = convenience;
        _library = library;
        _logger = logger;
    }

    public async Task<ExperienceWriteResult> IngestExperienceAsync(
        MemoryIngestionRequest request,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[Librarian] Ingest workspace={Workspace} title={Title}",
            request.WorkspaceId, request.Experience.Title);

        // 委托现有 Convenience 层完成写入（渐进迁移，后续将逻辑内联到此）
        var result = await _convenience.UpsertExperienceAsync(
            request.WorkspaceId, request.Experience, ct);

        // 如果 Experience 携带来源信息，写入 SourceReference
        if (!string.IsNullOrEmpty(request.Experience.SourceReference)
            && !string.IsNullOrEmpty(request.Experience.ReferenceType))
        {
            try
            {
                await _library.AddSourceReferenceAsync(new SourceReferenceCreateRequest(
                    request.WorkspaceId, "chapter", result.Chapter.ChapterId,
                    request.Experience.ReferenceType, request.Experience.SourceReference,
                    Label: request.Experience.Title), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Librarian] Failed to write SourceReference for chapter={ChapterId}",
                    result.Chapter.ChapterId);
            }
        }

        // 确保默认 books 存在
        try
        {
            await _library.EnsureDefaultBooksAsync(request.WorkspaceId,
                result.Book.LibraryId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Librarian] Default books seed skipped for library={LibId}",
                result.Book.LibraryId);
        }

        return result;
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
