using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

public interface ICodeIndexer
{
    Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default);

    Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);
}
