using System.Security.Cryptography;
using System.Text;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Index scope registry built on the existing <see cref="ICodeIndexStore"/>.
/// Handles idempotent ensure, parent/child overlap, and scope lifecycle.
/// Does NOT delete source files — only manages index registry state.
/// </summary>
public sealed class CodeIndexScopeRegistry : ICodeIndexScopeRegistry
{
    private readonly ICodeIndexStore _store;
    private readonly ICodeIndexScheduler? _scheduler;

    public CodeIndexScopeRegistry(ICodeIndexStore store, ICodeIndexScheduler? scheduler = null)
    {
        _store = store;
        _scheduler = scheduler;
    }

    public async Task<CodeIndexScope> EnsureScopeAsync(
        string workspaceId,
        string rootPath,
        ScopeSource source,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));

        if (!CodePathIdentity.TryNormalizeDirectoryPath(rootPath, out var normalized, out var pathError))
            throw new ArgumentException($"Invalid root path: {pathError}", nameof(rootPath));

        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"Directory not found: {normalized}");

        // Check if an exact same-folder scope already exists (idempotent).
        var existing = await FindExactScopeAsync(workspaceId, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        // Check if a parent scope already covers this directory.
        var covering = await FindCoveringScopeAsync(workspaceId, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (covering is not null)
        {
            // Create a covered child scope marker — no separate indexing needed.
            var childScope = new CodeIndexScope(
                workspaceId,
                BuildScopeId(workspaceId, normalized),
                normalized,
                ScopeState.Covered,
                source,
                displayName ?? GetFallbackName(normalized),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            // Persist as a covered project record.
            await _store.UpsertProjectAsync(new CodeProjectRecord(
                workspaceId,
                childScope.ScopeId,
                normalized,
                CodeProjectStatus.Active,
                childScope.DisplayName,
                childScope.AddedAtUtc,
                childScope.UpdatedAtUtc,
                Source: childScope.Source,
                ScopeState: childScope.State), cancellationToken).ConfigureAwait(false);

            return childScope;
        }

        // Check if any auto children exist — if we're registering a parent, cover them.
        var scopes = await ListScopesAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        foreach (var scope in scopes)
        {
            if (scope.State == ScopeState.Active
                && scope.Source == ScopeSource.Auto
                && IsChildOf(scope.RootPath, normalized))
            {
                await _store.UpsertProjectAsync(new CodeProjectRecord(
                    workspaceId,
                    scope.ScopeId,
                    scope.RootPath,
                    CodeProjectStatus.Active,
                    scope.DisplayName,
                    scope.AddedAtUtc,
                    DateTimeOffset.UtcNow,
                    Source: scope.Source,
                    ScopeState: ScopeState.Covered), cancellationToken).ConfigureAwait(false);
            }
        }

        // Create new active scope.
        var newScope = new CodeIndexScope(
            workspaceId,
            BuildScopeId(workspaceId, normalized),
            normalized,
            ScopeState.Active,
            source,
            displayName ?? GetFallbackName(normalized),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        await _store.UpsertProjectAsync(new CodeProjectRecord(
            workspaceId,
            newScope.ScopeId,
            normalized,
            CodeProjectStatus.Active,
            newScope.DisplayName,
            newScope.AddedAtUtc,
            newScope.UpdatedAtUtc,
            Source: newScope.Source,
            ScopeState: newScope.State), cancellationToken).ConfigureAwait(false);

        // Auto-detected scopes should be indexed in the background.
        if (source == ScopeSource.Auto)
        {
            _scheduler?.Enqueue(workspaceId, newScope.ScopeId);
        }

        return newScope;
    }

    public async Task<IReadOnlyList<CodeIndexScope>> ListScopesAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var projects = await _store.ListProjectsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return projects
            .Where(p => p.Status != CodeProjectStatus.Removed)
            .Select(ToScope)
            .ToList();
    }

    public async Task<CodeIndexResult> ForgetScopeAsync(
        string workspaceId,
        string scopeId,
        bool removeIndexData = true,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(workspaceId, scopeId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
            return new CodeIndexResult(false, CodeIndexStatus.Failed, "Scope not found.",
                WorkspaceId: workspaceId, ProjectId: scopeId);

        await _store.RemoveProjectAsync(workspaceId, scopeId, removeIndexData, cancellationToken)
            .ConfigureAwait(false);

        return new CodeIndexResult(true, CodeIndexStatus.Completed, "Scope removed.",
            WorkspaceId: workspaceId, ProjectId: scopeId);
    }

    public async Task<CodeIndexScope?> GetScopeAsync(
        string workspaceId,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(workspaceId, scopeId, cancellationToken)
            .ConfigureAwait(false);
        return project is null || project.Status == CodeProjectStatus.Removed ? null : ToScope(project);
    }

    public async Task<CodeIndexScope?> FindCoveringScopeAsync(
        string workspaceId,
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        if (!CodePathIdentity.TryNormalizeDirectoryPath(directoryPath, out var normalized, out _))
            return null;

        var scopes = await ListScopesAsync(workspaceId, cancellationToken).ConfigureAwait(false);

        // Find the tightest parent scope that covers this directory.
        CodeIndexScope? best = null;
        foreach (var scope in scopes)
        {
            if (scope.State != ScopeState.Active)
                continue;
            if (!IsParentOf(scope.RootPath, normalized))
                continue;
            if (best is null || scope.RootPath.Length > best.RootPath.Length)
                best = scope;
        }

        return best;
    }

    private async Task<CodeIndexScope?> FindExactScopeAsync(
        string workspaceId,
        string normalizedRootPath,
        CancellationToken cancellationToken)
    {
        var scopes = await ListScopesAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return scopes.FirstOrDefault(s =>
            string.Equals(s.RootPath, normalizedRootPath, CodePathIdentity.PathComparison));
    }

    private static bool IsParentOf(string parentPath, string childPath)
    {
        if (!childPath.StartsWith(parentPath, CodePathIdentity.PathComparison))
            return false;
        if (childPath.Length == parentPath.Length)
            return false;
        var after = childPath[parentPath.Length];
        return after == Path.DirectorySeparatorChar || after == Path.AltDirectorySeparatorChar;
    }

    private static bool IsChildOf(string childPath, string parentPath) =>
        IsParentOf(parentPath, childPath);

    private static CodeIndexScope ToScope(CodeProjectRecord p) =>
        new(p.WorkspaceId, p.ProjectId, p.ProjectPath,
            p.ScopeState ?? MapStatus(p.Status),
            p.Source ?? ScopeSource.Manual,
            p.DisplayName, p.AddedAtUtc, p.UpdatedAtUtc);

    private static ScopeState MapStatus(CodeProjectStatus status) => status switch
    {
        CodeProjectStatus.Active => ScopeState.Active,
        CodeProjectStatus.Failed => ScopeState.Failed,
        CodeProjectStatus.Removed => ScopeState.Removed,
        _ => ScopeState.Covered,
    };

    private static string BuildScopeId(string workspaceId, string rootPath)
    {
        var input = $"{workspaceId}|{rootPath}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"scope-{Convert.ToHexStringLower(hash)[..12]}";
    }

    private static string GetFallbackName(string rootPath) =>
        Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
