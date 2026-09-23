namespace PuddingCodeIntelligence.Contracts;

/// <summary>How a code index scope was created.</summary>
public enum ScopeSource
{
    /// <summary>Automatically detected by project root detection.</summary>
    Auto,
    /// <summary>Explicitly registered by a user or agent tool.</summary>
    Manual,
    /// <summary>Pinned by a user and excluded from parent scope auto-merge.</summary>
    Pinned,
}

/// <summary>Current state of a code index scope.</summary>
public enum ScopeState
{
    /// <summary>Active — queries should use this scope's index.</summary>
    Active,
    /// <summary>Covered by a parent scope — no separate indexing needed.</summary>
    Covered,
    /// <summary>Removed and no longer in the registry.</summary>
    Removed,
    /// <summary>Indexing failed and the scope needs attention.</summary>
    Failed,
}

/// <summary>A registered code index scope representing a project root directory.</summary>
public sealed record CodeIndexScope(
    string WorkspaceId,
    string ScopeId,
    string RootPath,
    ScopeState State,
    ScopeSource Source,
    string? DisplayName = null,
    DateTimeOffset? AddedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null);
