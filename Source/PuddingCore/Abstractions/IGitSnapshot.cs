namespace PuddingCode.Abstractions;

/// <summary>
/// Git-based snapshot and rollback service.
/// Creates automatic checkpoints before tool operations so the user can /undo.
/// </summary>
public interface IGitSnapshot
{
    /// <summary>Whether the project root is inside a Git repository.</summary>
    bool IsGitRepo { get; }

    /// <summary>Initialize a Git repo in the project root if one doesn't exist.</summary>
    Task EnsureRepoAsync(CancellationToken ct = default);

    /// <summary>
    /// Create a named snapshot (auto-commit of all changes).
    /// Returns the short commit hash, or null if nothing to commit.
    /// </summary>
    Task<string?> CreateSnapshotAsync(string label, CancellationToken ct = default);

    /// <summary>
    /// Undo the last N snapshots (soft reset, changes go back to working tree).
    /// Returns the number of snapshots actually undone.
    /// </summary>
    Task<int> UndoAsync(int count = 1, CancellationToken ct = default);

    /// <summary>
    /// List recent snapshots (most recent first).
    /// </summary>
    Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(int maxCount = 10, CancellationToken ct = default);

    /// <summary>
    /// Restore the working tree to a specific snapshot by hash.
    /// Uses git reset --hard; the caller should warn the user.
    /// </summary>
    Task<bool> RestoreAsync(string commitHash, CancellationToken ct = default);
}

/// <summary>A single snapshot entry from the Git log.</summary>
public sealed record SnapshotEntry(
    string Hash,
    string ShortHash,
    string Label,
    DateTimeOffset Timestamp);
