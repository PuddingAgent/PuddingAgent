namespace PuddingCodeIntelligence.Services.CodeIndex;

/// <summary>Kind of file-system change observed for a single path inside a code index scope.</summary>
public enum IndexChangeKind
{
    /// <summary>The path did not exist before and now exists.</summary>
    Created,

    /// <summary>The path existed before and its content or metadata changed.</summary>
    Changed,

    /// <summary>The path no longer exists.</summary>
    Deleted,

    /// <summary>The path was renamed; <see cref="IndexChange.OldFullPath"/> carries the previous location.</summary>
    Renamed,
}

/// <summary>
/// A single, minimal observation of a file-system change inside one code index scope.
/// <para>
/// Directory-level changes (a directory itself being created, deleted or renamed) are reported
/// with <see cref="IsDirectory"/> set, so that later slices can drive calibration from them.
/// </para>
/// <para>
/// An <see cref="IndexChange"/> carries <b>no</b> file content, no fingerprint and no index version:
/// those belong to the incremental-commit slice (U3-B). Capture must stay cheap and non-blocking.
/// </para>
/// </summary>
/// <param name="WorkspaceId">Workspace that owns the scope.</param>
/// <param name="ScopeId">Scope the observation belongs to.</param>
/// <param name="FullPath">Absolute path of the affected entry (the new path for <see cref="IndexChangeKind.Renamed"/>).</param>
/// <param name="Kind">What happened.</param>
/// <param name="OldFullPath">Previous absolute path; non-null only for <see cref="IndexChangeKind.Renamed"/>.</param>
/// <param name="IsDirectory">Whether the affected entry is a directory (best effort; see <c>CodeIndexWatcher</c>).</param>
/// <param name="Sequence">Monotonically increasing observation sequence allocated by the scope state.</param>
/// <param name="ObservedAtUtc">When the change was observed.</param>
public sealed record IndexChange(
    string WorkspaceId,
    string ScopeId,
    string FullPath,
    IndexChangeKind Kind,
    string? OldFullPath,
    bool IsDirectory,
    long Sequence,
    DateTimeOffset ObservedAtUtc);
