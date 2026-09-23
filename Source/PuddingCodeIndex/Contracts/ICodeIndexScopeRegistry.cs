using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

/// <summary>
/// Maintains code index scope records. Supports idempotent ensure, parent/child
/// overlap rules, and scope lifecycle (active / covered / removed / failed).
/// </summary>
public interface ICodeIndexScopeRegistry
{
    /// <summary>
    /// Ensure a scope for the given directory. If the same canonical folder
    /// already exists, return it idempotently. If a parent scope covers this
    /// directory, mark it as Covered. If an existing auto child exists and
    /// we're ensuring a parent, mark the child as Covered and activate the parent.
    /// </summary>
    Task<CodeIndexScope> EnsureScopeAsync(
        string workspaceId,
        string rootPath,
        ScopeSource source,
        string? displayName = null,
        CancellationToken cancellationToken = default);

    /// <summary>List all active and covered scopes for the workspace.</summary>
    Task<IReadOnlyList<CodeIndexScope>> ListScopesAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a scope from the registry. Does not delete source files.
    /// Optionally removes associated index data.
    /// </summary>
    Task<CodeIndexResult> ForgetScopeAsync(
        string workspaceId,
        string scopeId,
        bool removeIndexData = true,
        CancellationToken cancellationToken = default);

    /// <summary>Get the current status of a scope.</summary>
    Task<CodeIndexScope?> GetScopeAsync(
        string workspaceId,
        string scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the active scope that covers a given directory path. Returns null
    /// if no scope covers it.
    /// </summary>
    Task<CodeIndexScope?> FindCoveringScopeAsync(
        string workspaceId,
        string directoryPath,
        CancellationToken cancellationToken = default);
}
