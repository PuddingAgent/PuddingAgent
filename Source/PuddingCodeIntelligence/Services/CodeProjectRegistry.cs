using System.Security.Cryptography;
using System.Text;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Registers source directories as code-intelligence projects without assuming
/// that the source directory lives inside the agent process workspace.
/// </summary>
public sealed class CodeProjectRegistry : ICodeProjectRegistry
{
    private readonly ICodeIndexStore _store;
    private readonly ICodeWorkspaceResolver _workspaceResolver;

    public CodeProjectRegistry(
        ICodeIndexStore store,
        ICodeWorkspaceResolver workspaceResolver)
    {
        _store = store;
        _workspaceResolver = workspaceResolver;
    }

    public async Task<CodeIndexResult> AddProjectAsync(
        CodeProjectAddRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "WorkspaceId is required.");

        if (string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            return new CodeIndexResult(
                false,
                CodeIndexStatus.Failed,
                "ProjectPath is required.",
                WorkspaceId: request.WorkspaceId);
        }

        if (!CodePathIdentity.TryNormalizeDirectoryPath(request.ProjectPath, out var projectPath, out var pathError))
        {
            return new CodeIndexResult(
                false,
                CodeIndexStatus.Failed,
                $"ProjectPath is invalid: {pathError}",
                WorkspaceId: request.WorkspaceId,
                ProjectId: request.ProjectId);
        }

        if (!Directory.Exists(projectPath))
        {
            return new CodeIndexResult(
                false,
                CodeIndexStatus.Failed,
                $"Project directory does not exist: {projectPath}",
                WorkspaceId: request.WorkspaceId,
                ProjectId: request.ProjectId);
        }

        var projectId = string.IsNullOrWhiteSpace(request.ProjectId)
            ? BuildProjectId(request.WorkspaceId, projectPath)
            : request.ProjectId.Trim();
        var now = DateTimeOffset.UtcNow;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? GetFallbackDisplayName(projectPath, projectId)
            : request.DisplayName;

        var project = new CodeProjectRecord(
            request.WorkspaceId,
            projectId,
            projectPath,
            CodeProjectStatus.Active,
            displayName,
            now,
            now);

        await _store.UpsertProjectAsync(project, cancellationToken).ConfigureAwait(false);

        var descriptor = await _workspaceResolver.ResolveWorkspaceAsync(
            request.WorkspaceId,
            projectId,
            cancellationToken).ConfigureAwait(false);

        if (descriptor?.SolutionPath is not null && string.IsNullOrWhiteSpace(request.DisplayName))
        {
            var solutionName = Path.GetFileNameWithoutExtension(descriptor.SolutionPath);
            if (!string.IsNullOrWhiteSpace(solutionName) &&
                !string.Equals(solutionName, displayName, StringComparison.Ordinal))
            {
                await _store.UpsertProjectAsync(
                    project with { DisplayName = solutionName, UpdatedAtUtc = DateTimeOffset.UtcNow },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new CodeIndexResult(
            true,
            CodeIndexStatus.Pending,
            "Project registered successfully.",
            WorkspaceId: request.WorkspaceId,
            ProjectId: projectId);
    }

    public async Task<CodeIndexResult> RemoveProjectAsync(
        CodeProjectRemoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.ProjectId))
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "WorkspaceId and ProjectId are required.");

        var existing = await _store.GetProjectAsync(
            request.WorkspaceId,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new CodeIndexResult(
                false,
                CodeIndexStatus.Failed,
                "Project is not registered.",
                WorkspaceId: request.WorkspaceId,
                ProjectId: request.ProjectId);
        }

        await _store.RemoveProjectAsync(
            request.WorkspaceId,
            request.ProjectId,
            request.RemoveIndexData,
            cancellationToken).ConfigureAwait(false);

        return new CodeIndexResult(
            true,
            CodeIndexStatus.Completed,
            "Project removed.",
            WorkspaceId: request.WorkspaceId,
            ProjectId: request.ProjectId);
    }

    public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string workspaceId,
        CancellationToken cancellationToken = default) =>
        _store.ListProjectsAsync(workspaceId, cancellationToken);

    public Task<CodeProjectRecord?> GetProjectAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        _store.GetProjectAsync(workspaceId, projectId, cancellationToken);

    private static string BuildProjectId(string workspaceId, string projectPath)
    {
        var normalizedWorkspace = workspaceId.Trim();
        var normalizedPath = CodePathIdentity.NormalizePathForIdentity(projectPath);
        var input = $"{normalizedWorkspace}::{normalizedPath}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string GetFallbackDisplayName(string projectPath, string projectId)
    {
        var name = Path.GetFileName(projectPath);
        return string.IsNullOrWhiteSpace(name) ? projectId : name;
    }

}
