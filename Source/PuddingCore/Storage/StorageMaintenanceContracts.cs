namespace PuddingCode.Storage;

/// <summary>
/// Stable target identifiers exposed by the Core storage-management API.
/// Targets are deliberately semantic rather than arbitrary table names so a
/// client can never turn the cleanup endpoint into a general SQL delete API.
/// </summary>
public static class StorageMaintenanceTargetIds
{
    public const string Telemetry = "diagnostics.telemetry";
    public const string RuntimeActivity = "diagnostics.runtime-activity";
    public const string DuplicateIndexes = "platform.duplicate-indexes";
    public const string ObsoleteCodeIndexScopes = "code-index.obsolete-scopes";
}

public sealed record StorageDatabaseAnalysis
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required long TotalBytes { get; init; }
    public required IReadOnlyList<StorageDatabaseFileSnapshot> Databases { get; init; }
    public required IReadOnlyList<StorageMaintenanceItemSnapshot> Items { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record StorageDatabaseFileSnapshot
{
    public required string DatabaseId { get; init; }
    public required string DisplayName { get; init; }
    public required string RelativePath { get; init; }
    public required long MainBytes { get; init; }
    public required long WalBytes { get; init; }
    public required long SharedMemoryBytes { get; init; }
    public long TotalBytes => MainBytes + WalBytes + SharedMemoryBytes;
    public required long PageSizeBytes { get; init; }
    public required long PageCount { get; init; }
    public required long FreePageCount { get; init; }
    public long ReclaimableFreeBytes => PageSizeBytes * FreePageCount;
}

public sealed record StorageMaintenanceItemSnapshot
{
    public required string ItemId { get; init; }
    public required string DatabaseId { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required long RowCount { get; init; }
    public bool RowCountIsApproximate { get; init; }
    public long? AllocatedBytes { get; init; }
    public required bool CanClean { get; init; }
    public required bool IsProtected { get; init; }
    public string? ProtectionReason { get; init; }
    public int? DefaultRetentionDays { get; init; }
}

public sealed record StorageCleanupPreviewRequest
{
    public required IReadOnlyList<string> TargetIds { get; init; }
    public int RetentionDays { get; init; } = 14;
    public bool CompactAfterCleanup { get; init; } = true;
}

public sealed record StorageCleanupPreview
{
    public required Guid PreviewId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required int RetentionDays { get; init; }
    public required bool CompactAfterCleanup { get; init; }
    public required IReadOnlyList<StorageCleanupTargetPreview> Targets { get; init; }
    public required long CandidateRows { get; init; }
    public long? EstimatedReclaimableBytes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record StorageCleanupTargetPreview
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required long CandidateRows { get; init; }
    public long? EstimatedReclaimableBytes { get; init; }
    public required string Summary { get; init; }
}

public sealed record StorageCleanupExecuteRequest
{
    public required Guid PreviewId { get; init; }
}

public sealed record StorageCleanupResult
{
    public required Guid PreviewId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required long DeletedRows { get; init; }
    public required int DroppedIndexes { get; init; }
    public required int RemovedCodeIndexScopes { get; init; }
    public required long BytesBefore { get; init; }
    public required long BytesAfter { get; init; }
    public long ReleasedBytes => Math.Max(0, BytesBefore - BytesAfter);
    public required IReadOnlyList<string> CompactedDatabases { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required StorageDatabaseAnalysis Analysis { get; init; }
}

public interface IStorageMaintenanceService
{
    Task<StorageDatabaseAnalysis> AnalyzeAsync(CancellationToken cancellationToken = default);

    Task<StorageCleanupPreview> PreviewCleanupAsync(
        StorageCleanupPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<StorageCleanupResult> ExecuteCleanupAsync(
        Guid previewId,
        CancellationToken cancellationToken = default);
}
