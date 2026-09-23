using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Scope resolver: searches existing scopes, then falls back to project root
/// detection and auto-registration when no scope matches.
/// </summary>
public sealed class CodeIndexScopeResolver : ICodeIndexScopeResolver
{
    private readonly ICodeIndexScopeRegistry _registry;
    private readonly ICodeProjectRootDetector _detector;

    public CodeIndexScopeResolver(
        ICodeIndexScopeRegistry registry,
        ICodeProjectRootDetector detector)
    {
        _registry = registry;
        _detector = detector;
    }

    public async Task<ScopeResolution?> ResolveAsync(
        string workspaceId,
        string? filePath = null,
        string? scopePath = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        // Explicit scope id is the most precise.
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            var scope = await _registry.GetScopeAsync(workspaceId, scopeId, cancellationToken)
                .ConfigureAwait(false);
            return scope is not null
                ? new ScopeResolution(scope, false, false)
                : null;
        }

        // Determine starting directory.
        var startDir = ResolveStartDirectory(filePath, scopePath);
        if (startDir is null)
            return null;

        // Search existing scopes first.
        var covering = await _registry.FindCoveringScopeAsync(workspaceId, startDir, cancellationToken)
            .ConfigureAwait(false);
        if (covering is not null)
            return new ScopeResolution(covering, false, false);

        // Auto-detect project root.
        var detected = await _detector.DetectRootAsync(startDir, cancellationToken).ConfigureAwait(false);
        if (detected is null)
            return null;

        // Check if the detected root is already registered.
        covering = await _registry.FindCoveringScopeAsync(workspaceId, detected, cancellationToken)
            .ConfigureAwait(false);
        if (covering is not null)
            return new ScopeResolution(covering, true, false);

        return null; // Caller should use ResolveAndEnsureAsync to create the scope.
    }

    public async Task<ScopeResolution> ResolveAndEnsureAsync(
        string workspaceId,
        string? filePath = null,
        string? scopePath = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        // Explicit scope id.
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            var scope = await _registry.GetScopeAsync(workspaceId, scopeId, cancellationToken)
                .ConfigureAwait(false);
            if (scope is not null)
                return new ScopeResolution(scope, false, false);

            throw new InvalidOperationException($"Scope '{scopeId}' not found.");
        }

        var startDir = ResolveStartDirectory(filePath, scopePath)
            ?? Directory.GetCurrentDirectory();

        // Check existing.
        var covering = await _registry.FindCoveringScopeAsync(workspaceId, startDir, cancellationToken)
            .ConfigureAwait(false);
        if (covering is not null)
            return new ScopeResolution(covering, false, false);

        // Auto-detect.
        var detected = await _detector.DetectRootAsync(startDir, cancellationToken).ConfigureAwait(false);
        if (detected is null)
        {
            // No project root detected — fall back to the start directory itself as a scope.
            detected = startDir;
        }

        // Double-check after detection (might already be registered).
        covering = await _registry.FindCoveringScopeAsync(workspaceId, detected, cancellationToken)
            .ConfigureAwait(false);
        if (covering is not null)
            return new ScopeResolution(covering, true, false);

        var newScope = await _registry.EnsureScopeAsync(
            workspaceId, detected, ScopeSource.Auto, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ScopeResolution(newScope, true, true);
    }

    private static string? ResolveStartDirectory(string? filePath, string? scopePath)
    {
        if (!string.IsNullOrWhiteSpace(scopePath))
        {
            if (CodePathIdentity.TryNormalizeDirectoryPath(scopePath, out var dir, out _)
                && Directory.Exists(dir))
                return dir;

            // Maybe it's a file path — treat parent as the start.
            if (File.Exists(scopePath))
                return Path.GetDirectoryName(Path.GetFullPath(scopePath));
        }

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            return Path.GetDirectoryName(Path.GetFullPath(filePath));

        return Directory.GetCurrentDirectory();
    }
}
