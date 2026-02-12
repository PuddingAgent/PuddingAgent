using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

public interface ICodeProjectRegistry
{
    Task<CodeIndexResult> AddProjectAsync(
        CodeProjectAddRequest request,
        CancellationToken cancellationToken = default);

    Task<CodeIndexResult> RemoveProjectAsync(
        CodeProjectRemoveRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<CodeProjectRecord?> GetProjectAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);
}
