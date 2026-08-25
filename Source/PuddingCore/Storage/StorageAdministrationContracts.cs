namespace PuddingCode.Storage;

/// <summary>
/// ADR-076 存储管理语义类型 ID。所有清理/估算对象只允许来自该白名单，
/// 客户端永远不能提供表名、路径或 SQL 片段。
/// </summary>
public static class StorageAdminTargetIds
{
    public const string DebugPayload = "diagnostics.debug-payload";
    public const string TelemetryRaw = "diagnostics.telemetry-raw";
    public const string ContextLayerRaw = "diagnostics.context-layer-raw";
    public const string RuntimeActivity = "diagnostics.runtime-activity";
    public const string LogsVerbose = "diagnostics.logs.verbose";
    public const string LogsError = "diagnostics.logs.error";
    public const string Rollups = "diagnostics.rollups";
    public const string ObsoleteCodeIndexScopes = "code-index.obsolete-scopes";
    public const string RedundantIndexes = "storage.redundant-indexes";

    /// <summary>Evidence 保留路径：只走独立证据策略，永不进入人工清理选择器。</summary>
    public const string ConversationEventsEvidence = "evidence.conversation-events";

    /// <summary>旧 /databases 端点兼容 ID（过渡期翻译到新语义集合，见 ADR-076 §2.1）。</summary>
    public const string LegacyTelemetry = "diagnostics.telemetry";
    public const string LegacyRuntimeActivity = "diagnostics.runtime-activity";
    public const string LegacyDuplicateIndexes = "platform.duplicate-indexes";
}

/// <summary>数据安全等级（ADR-076 决策 4）。</summary>
public enum StorageSafetyLevel
{
    Disposable = 0,
    Derived = 1,
    Evidence = 2,
    UserData = 3,
}

/// <summary>估算状态：后台采样未覆盖 / 已更新 / 本次不可用。</summary>
public enum StorageEstimateState
{
    Estimating = 0,
    Updated = 1,
    Unavailable = 2,
}

/// <summary>清理作业状态机（ADR-076 §6.3）。</summary>
public enum StorageCleanupJobStatus
{
    Queued = 0,
    Running = 1,
    PausedBusy = 2,
    NeedsConfirmation = 3,
    Cancelling = 4,
    Completed = 5,
    Partial = 6,
    Failed = 7,
    Cancelled = 8,
}

/// <summary>后台库存刷新状态。</summary>
public enum StorageInventoryRefreshState
{
    Idle = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

// ─── 目录投影 ─────────────────────────────────────────────────────

public sealed record StorageDataClassDto
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required StorageSafetyLevel SafetyLevel { get; init; }
    public required string SafetyLevelName { get; init; }
    /// <summary>目录版本；Preview/Execute 必须携带并校验一致。</summary>
    public required int CatalogVersion { get; init; }
    public required bool ManualCleanupAllowed { get; init; }
    public required bool AutomaticCleanupAllowed { get; init; }
    public bool Protected { get; init; }
    public string? ProtectionReason { get; init; }
    public int? DefaultRetentionDays { get; init; }
    public int? MinRetentionDays { get; init; }
    public int? MaxRetentionDays { get; init; }
    /// <summary>该类型默认自动清理是否需要先落地长期聚合（当前未实现时默认关闭）。</summary>
    public bool RequiresRollupBeforeAutomatic { get; init; }
}

// ─── 库存快照 ─────────────────────────────────────────────────────

public sealed record StorageInventoryDatabaseDto
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
    public long ReusableFreeBytes => PageSizeBytes * FreePageCount;
}

public sealed record StorageInventoryClassDto
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>估算占用字节；null 表示本分类暂不可估算。</summary>
    public long? EstimatedBytes { get; init; }
    /// <summary>估算行数/文件数；null 表示暂不可估算。</summary>
    public long? EstimatedRows { get; init; }
    public DateTimeOffset? OldestUtc { get; init; }
    public DateTimeOffset? NewestUtc { get; init; }
    public required StorageEstimateState EstimateState { get; init; }
    public required DateTimeOffset? UpdatedAtUtc { get; init; }
}

public sealed record StorageInventorySnapshotDto
{
    public required Guid SnapshotId { get; init; }
    /// <summary>单调递增 revision；前端对相同 revision 短路重复渲染。</summary>
    public required long Revision { get; init; }
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required IReadOnlyList<StorageInventoryDatabaseDto> Databases { get; init; }
    public required IReadOnlyList<StorageInventoryClassDto> Classes { get; init; }
    public required bool IsRefreshing { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

// ─── 刷新请求 ─────────────────────────────────────────────────────

public sealed record StorageInventoryRefreshStatusDto
{
    public required Guid RefreshId { get; init; }
    public required StorageInventoryRefreshState State { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    /// <summary>当前快照在刷新期间的最新 revision。</summary>
    public required long SnapshotRevision { get; init; }
}

// ─── 趋势历史 ─────────────────────────────────────────────────────

public sealed record StorageInventoryTrendPointDto
{
    public required DateTimeOffset CapturedAtUtc { get; init; }
    /// <summary>TargetId → 估算字节（仅包含当时已知的分类）。</summary>
    public required IReadOnlyDictionary<string, long> ClassBytes { get; init; }
    public required long DatabaseTotalBytes { get; init; }
}

// ─── 保留策略 ─────────────────────────────────────────────────────

public sealed record StorageRetentionPolicyDto
{
    public required int PolicyRevision { get; init; }
    public required bool AutomaticCleanupEnabled { get; init; }
    public required int RunIntervalHours { get; init; }
    public required int StartupDelaySeconds { get; init; }
    public required DateTimeOffset? LastCompletedAtUtc { get; init; }
    public DateTimeOffset? NextRunEstimateUtc { get; init; }
    public required IReadOnlyList<StorageRetentionPolicyTargetDto> Targets { get; init; }
    /// <summary>策略加载/校验失败时的告警（fail closed：自动作业暂停）。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record StorageRetentionPolicyTargetDto
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required bool Enabled { get; init; }
    public int? RetentionDays { get; init; }
    public required bool AutomaticCleanupAllowed { get; init; }
    public int? DefaultRetentionDays { get; init; }
    public int? MinRetentionDays { get; init; }
    public int? MaxRetentionDays { get; init; }
}

public sealed record StorageRetentionPolicyUpdateRequest
{
    public required int ExpectedRevision { get; init; }
    public bool? AutomaticCleanupEnabled { get; init; }
    public IReadOnlyList<StorageRetentionPolicyTargetUpdateDto>? Targets { get; init; }
}

public sealed record StorageRetentionPolicyTargetUpdateDto
{
    public required string TargetId { get; init; }
    public bool? Enabled { get; init; }
    /// <summary>0 不合法；禁用自动清理应使用 Enabled=false。</summary>
    public int? RetentionDays { get; init; }
}

// ─── 清理 Preview / Job ───────────────────────────────────────────

public sealed record StorageCleanupPreviewRequestDto
{
    /// <summary>语义类型 ID，仅允许 ManualCleanupAllowed=true 的目录项。</summary>
    public required IReadOnlyList<string> TargetIds { get; init; }
    /// <summary>olderThanDays 与 cutoffUtc 二选一；服务器固定为 cutoffUtc。</summary>
    public int? OlderThanDays { get; init; }
    public DateTimeOffset? CutoffUtc { get; init; }
}

public sealed record StorageCleanupTargetPreviewDto
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required string ActionSummary { get; init; }
    /// <summary>有界候选估算（计数上限内精确，超出标记 Truncated）。</summary>
    public required long EstimatedCandidateRows { get; init; }
    public bool CandidatesTruncated { get; init; }
    public long? EstimatedBytes { get; init; }
    public DateTimeOffset? OldestUtc { get; init; }
}

public sealed record StorageCleanupPreviewDto
{
    public required Guid PreviewId { get; init; }
    public required int CatalogVersion { get; init; }
    public required int PolicyRevision { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required DateTimeOffset CutoffUtc { get; init; }
    public required IReadOnlyList<StorageCleanupTargetPreviewDto> Targets { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool HasCandidates { get; init; }
}

public sealed record StorageCleanupJobCreateRequest
{
    public required Guid PreviewId { get; init; }
    /// <summary>幂等键：相同 requestId 的重复提交返回同一作业。</summary>
    public required string RequestId { get; init; }
}

public sealed record StorageCleanupJobProgressDto
{
    public required long DiscoveredRows { get; init; }
    public required long ProcessedRows { get; init; }
    public required long DeletedRows { get; init; }
    public required long ClearedRows { get; init; }
    public required long SkippedRows { get; init; }
    public required long FailedRows { get; init; }
    public required long DeletedFiles { get; init; }
    public required long ReusableBytesEstimate { get; init; }
    /// <summary>剩余估算（基于最后一轮有界探测；null=未知）。</summary>
    public long? RemainingRowsEstimate { get; init; }
}

public sealed record StorageCleanupJobDto
{
    public required Guid JobId { get; init; }
    public required string Trigger { get; init; }
    public required StorageCleanupJobStatus Status { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public required DateTimeOffset CutoffUtc { get; init; }
    public required IReadOnlyList<string> TargetIds { get; init; }
    public required StorageCleanupJobProgressDto Progress { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StorageCleanupJobEventDto
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Kind { get; init; }
    public string? TargetId { get; init; }
    public IReadOnlyDictionary<string, long>? Counters { get; init; }
    public string? Message { get; init; }
}

// ─── 稳定错误码（ADR-076 §11）─────────────────────────────────────

public static class StorageAdminErrorCodes
{
    public const string PreviewExpired = "storage_preview_expired";
    public const string PreviewConsumed = "storage_preview_consumed";
    public const string PreviewNoCandidates = "storage_preview_no_candidates";
    public const string PolicyConflict = "storage_policy_conflict";
    public const string TargetProtected = "storage_target_protected";
    public const string TargetUnknown = "storage_target_unknown";
    public const string SchemaUnavailable = "storage_schema_unavailable";
    public const string MaintenanceBusy = "storage_maintenance_busy";
    public const string JobNotCancellable = "storage_job_not_cancellable";
    public const string DataRootUnsafe = "storage_dataroot_unsafe";
    public const string CompactionOfflineRequired = "storage_compaction_offline_required";
    public const string RefreshAlreadyRunning = "storage_refresh_merged";
}
