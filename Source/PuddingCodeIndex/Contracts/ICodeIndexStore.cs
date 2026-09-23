using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIndex.Contracts;

public interface ICodeIndexStore
{
    Task UpsertProjectAsync(
        CodeProjectRecord project,
        CancellationToken cancellationToken = default);

    Task RemoveProjectAsync(
        string workspaceId,
        string projectId,
        bool removeIndexedArtifacts = true,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<CodeProjectRecord?> GetProjectAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);

    Task UpdateProjectStatusAsync(
        string workspaceId,
        string projectId,
        CodeProjectStatus status,
        string? statusMessage = null,
        CancellationToken cancellationToken = default);

    Task UpsertFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeFileRecord> files,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a set of files from the index in <b>one transaction</b>: their file records, every symbol
    /// declared in them, and every relation/reference those symbols take part in.
    /// <para>
    /// Idempotent by design: a path that is not indexed (already removed, never indexed, or simply unknown)
    /// is a safe no-op — no exception, no failure. The return value is the number of <b>file records</b>
    /// actually deleted, so a caller can distinguish "a removal was observed" from "something was removed".
    /// </para>
    /// </summary>
    /// <param name="workspaceId">Workspace that owns the project.</param>
    /// <param name="projectId">Project the files belong to.</param>
    /// <param name="filePaths">Absolute file paths to remove; empty or blank entries are ignored.</param>
    /// <param name="cancellationToken">Cancels the removal; nothing is committed when it fires.</param>
    /// <returns>Number of file records deleted (0 when none of the paths was indexed).</returns>
    Task<int> RemoveFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default);

    Task UpsertSymbolsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSymbolRecord> symbols,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeSymbolRecord>> GetSymbolsByFileAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken = default);

    Task ClearSymbolsForFileAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken = default);

    Task UpsertRelationsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeRelationRecord> relations,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeRelationRecord>> ListRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CodeRelationKind? relationKind = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CodeRelationKind? relationKind = null,
        CancellationToken cancellationToken = default);

    Task ClearRelationsAsync(
        string workspaceId,
        string projectId,
        string sourceSymbolId,
        CancellationToken cancellationToken = default);

    Task UpsertReferencesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeReferenceRecord> references,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeReferenceRecord>> ListReferencesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default);

    Task ClearReferencesAsync(
        string workspaceId,
        string projectId,
        string sourceSymbolId,
        CancellationToken cancellationToken = default);
}
