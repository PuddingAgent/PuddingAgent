using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

public interface ICodeWorkspaceResolver
{
    Task<CodeWorkspaceDescriptor?> ResolveWorkspaceAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeWorkspaceDescriptor>> ResolveWorkspacesByProjectPathAsync(
        string workspaceId,
        string projectPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeWorkspaceDescriptor>> ResolveWorkspacesAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}
