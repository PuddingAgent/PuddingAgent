using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

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
