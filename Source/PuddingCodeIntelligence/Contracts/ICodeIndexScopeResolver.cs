using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

/// <summary>
/// Resolves a query context (file path, directory path, or explicit scope id)
/// into a canonical index scope. Uses <see cref="ICodeIndexScopeRegistry"/> for
/// known scopes and <see cref="ICodeProjectRootDetector"/> for auto-detection
/// when no registered scope matches.
/// </summary>
public interface ICodeIndexScopeResolver
{
    /// <summary>
    /// Resolve the index scope for a given file path or directory. Returns the
    /// scope record and a flag indicating whether it was auto-detected.
    /// </summary>
    Task<ScopeResolution?> ResolveAsync(
        string workspaceId,
        string? filePath = null,
        string? scopePath = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve and ensure a scope exists. If no registered scope matches, detects
    /// the project root and creates an auto scope. Returns the scope and whether
    /// it was created right now.
    /// </summary>
    Task<ScopeResolution> ResolveAndEnsureAsync(
        string workspaceId,
        string? filePath = null,
        string? scopePath = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of scope resolution.</summary>
public sealed record ScopeResolution(
    CodeIndexScope? Scope,
    bool IsAutoDetected,
    bool IsNewlyCreated);
