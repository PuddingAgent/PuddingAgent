namespace PuddingDesktop.Storage;

public enum StorageCategoryKind
{
    Logs,
    DatabaseAndIndex,
    ConversationAndMemory,
    AssetsAndDownloads,
    Browser,
    Backups,
    Configuration,
    UnexpectedBuildOutput,
    Temporary,
    Other,
}

public sealed record StorageCategoryDefinition(
    StorageCategoryKind Kind,
    string DisplayName,
    string Description,
    string IconGlyph,
    int Order,
    bool CanClean = false);

public sealed record StorageCategorySnapshot
{
    public required StorageCategoryDefinition Definition { get; init; }
    public required long LogicalBytes { get; init; }
    public required long FileCount { get; init; }
}

public sealed record StorageScanWarning(
    string Path,
    string Message);

public sealed record StorageScanProgress(
    long ScannedFileCount,
    long ScannedBytes,
    string CurrentPath);

public sealed record StorageSnapshot
{
    public required string DataRoot { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required long LogicalBytes { get; init; }
    public long? AllocatedBytes { get; init; }
    public required long DriveTotalBytes { get; init; }
    public required long DriveFreeBytes { get; init; }
    public required IReadOnlyList<StorageCategorySnapshot> Categories { get; init; }
    public required IReadOnlyList<StorageScanWarning> Warnings { get; init; }
}

public sealed record ValidatedDataRoot(
    string DataRoot,
    string LogRoot,
    string DriveRoot);

public sealed record LogCleanupCandidate
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset LastWriteTimeUtc { get; init; }
    public required DateTimeOffset CreationTimeUtc { get; init; }
}

public sealed record LogCleanupPreview
{
    public required Guid PreviewId { get; init; }
    public required string DataRoot { get; init; }
    public required string LogRoot { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset CutoffUtc { get; init; }
    public required TimeSpan Retention { get; init; }
    public required IReadOnlyList<LogCleanupCandidate> Candidates { get; init; }
    public required long CandidateBytes { get; init; }
}

public sealed record LogCleanupProgress(
    int ProcessedFiles,
    int TotalFiles,
    long DeletedBytes,
    string CurrentPath);

public sealed record LogCleanupFailure(
    string RelativePath,
    string Error);

public sealed record LogCleanupResult
{
    public required Guid PreviewId { get; init; }
    public required int DeletedFiles { get; init; }
    public required long DeletedBytes { get; init; }
    public required int SkippedFiles { get; init; }
    public required int FailedFiles { get; init; }
    public required IReadOnlyList<LogCleanupFailure> Failures { get; init; }
}

public sealed class StorageSafetyException(string message) : InvalidOperationException(message);
