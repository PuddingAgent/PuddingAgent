using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

public interface ICodeQueryService
{
    Task<CodeIndexResult> GetProjectIndexStatusAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        int maxDepth = 3,
        CancellationToken cancellationToken = default);
}
