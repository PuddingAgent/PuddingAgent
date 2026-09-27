namespace PuddingDesktop.Foundation;

public sealed record StorageDatabase(
    string DatabaseId, string DisplayName, string RelativePath,
    long TotalBytes, long MainBytes, long WalBytes, long SharedMemoryBytes,
    long ReusableFreeBytes);

public sealed record StorageInventoryClass(
    string TargetId, string DisplayName, long? EstimatedBytes, long? EstimatedRows,
    DateTimeOffset? OldestUtc, DateTimeOffset? NewestUtc, string EstimateState, DateTimeOffset? UpdatedAtUtc)
{
    public string SizeText => StorageText.FormatBytes(EstimatedBytes);
    public string RowsText => EstimatedRows is null ? "行数未知" : $"{EstimatedRows:N0} 行/文件";
    public string EstimateText => StorageText.DescribeEstimateState(EstimateState);
}

/// <summary>The most recent cached inventory snapshot. Reading it never triggers a scan.</summary>
public sealed record StorageSnapshot(
    Guid SnapshotId, long Revision, int SchemaVersion, DateTimeOffset CapturedAtUtc, DateTimeOffset UpdatedAtUtc,
    bool IsRefreshing, IReadOnlyList<string> Warnings,
    IReadOnlyList<StorageDatabase> Databases, IReadOnlyList<StorageInventoryClass> Classes)
{
    public long TotalBytes => Databases.Sum(database => database.TotalBytes) + Classes.Sum(item => item.EstimatedBytes ?? 0);
    public long DatabaseBytes => Databases.Sum(database => database.TotalBytes);
    public long ClassBytes => Classes.Sum(item => item.EstimatedBytes ?? 0);
    public bool IsEmpty => Databases.Count == 0 && Classes.Count == 0;
}

public sealed record StorageTrendPoint(DateTimeOffset CapturedAtUtc, long DatabaseTotalBytes, IReadOnlyDictionary<string, long> ClassBytes);

public sealed record StorageDataClass(
    string TargetId, string DisplayName, string Description, string SafetyLevelName,
    int CatalogVersion, bool ManualCleanupAllowed, bool AutomaticCleanupAllowed, bool Protected,
    string ProtectionReason, int? DefaultRetentionDays, int? MinRetentionDays)
{
    public string SafetyText => StorageText.DescribeSafetyLevel(SafetyLevelName);
    public string CleanupText => StorageText.DescribeCleanupPermissions(ManualCleanupAllowed, AutomaticCleanupAllowed);
}

public sealed record StorageRefreshStatus(Guid RefreshId, string State, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc, long SnapshotRevision)
{
    public string StateText => StorageText.DescribeRefreshState(State);
}

public sealed record StorageRetentionTarget(
    string TargetId, string DisplayName, bool Enabled, int? RetentionDays, bool AutomaticCleanupAllowed,
    int? DefaultRetentionDays, int? MinRetentionDays, int? MaxRetentionDays)
{
    public string RangeText => StorageText.DescribeRetentionRange(MinRetentionDays, MaxRetentionDays, DefaultRetentionDays);
    public string EffectiveText => Enabled
        ? RetentionDays is null ? "启用（未设置天数）" : $"启用 · 保留 {RetentionDays} 天"
        : "未启用";
}

public sealed record StorageRetentionPolicy(
    int PolicyRevision, bool AutomaticCleanupEnabled, int RunIntervalHours, int StartupDelaySeconds,
    DateTimeOffset? LastCompletedAtUtc, DateTimeOffset? NextRunEstimateUtc,
    IReadOnlyList<StorageRetentionTarget> Targets, IReadOnlyList<string> Warnings);

/// <summary>A target edit. RetentionDays must be positive; disabling uses Enabled=false instead of 0.</summary>
public sealed record StorageRetentionTargetEdit(string TargetId, bool Enabled, int? RetentionDays);

public sealed record StorageRetentionPolicyUpdate(
    int ExpectedRevision, bool AutomaticCleanupEnabled, IReadOnlyList<StorageRetentionTargetEdit> Targets);

public sealed record StorageCleanupTargetEstimate(
    string TargetId, string DisplayName, string ActionSummary, long EstimatedCandidateRows,
    bool CandidatesTruncated, long? EstimatedBytes, DateTimeOffset? OldestUtc)
{
    public string CandidatesText => CandidatesTruncated
        ? $"≥ {EstimatedCandidateRows:N0} 行（已达计数上限，实际可能更多）"
        : $"{EstimatedCandidateRows:N0} 行";
}

/// <summary>A preview is Core's bounded estimate and expires; the page must not treat it as a promise.</summary>
public sealed record StorageCleanupEstimate(
    Guid PreviewId, int CatalogVersion, int PolicyRevision, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset CutoffUtc, bool HasCandidates,
    IReadOnlyList<string> Warnings, IReadOnlyList<StorageCleanupTargetEstimate> Targets)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
}

public sealed record StorageCleanupCounters(
    long DiscoveredRows, long ProcessedRows, long DeletedRows, long ClearedRows,
    long SkippedRows, long FailedRows, long DeletedFiles, long ReusableBytesEstimate,
    long? RemainingRowsEstimate)
{
    public string RemainingText => RemainingRowsEstimate is null ? "剩余未知" : $"剩余约 {RemainingRowsEstimate:N0} 行";
    public string SummaryText =>
        $"已处理 {ProcessedRows:N0}/{DiscoveredRows:N0} · 删除 {DeletedRows:N0} · 清空 {ClearedRows:N0} · " +
        $"跳过 {SkippedRows:N0} · 失败 {FailedRows:N0} · 文件 {DeletedFiles:N0} · 可复用 {StorageText.FormatBytes(ReusableBytesEstimate)}";
}

public sealed record StorageCleanupRun(
    Guid JobId, string Trigger, string Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc, DateTimeOffset CutoffUtc,
    IReadOnlyList<string> TargetIds, StorageCleanupCounters Progress,
    IReadOnlyList<string> Warnings, string ErrorCode, string ErrorMessage)
{
    public string StatusText => StorageText.DescribeJobStatus(Status);
    public bool IsTerminal => Status is "Completed" or "Partial" or "Failed" or "Cancelled";
    public bool NeedsConfirmation => Status == "NeedsConfirmation";
    public bool CanCancel => !IsTerminal && Status is not "Cancelling";
}

public sealed record StorageCleanupEvent(
    DateTimeOffset TimestampUtc, string Kind, string TargetId, string Message,
    IReadOnlyDictionary<string, long> Counters)
{
    public string KindText => StorageText.DescribeEventKind(Kind);
    public string CountersText => Counters.Count == 0
        ? ""
        : " · " + string.Join(" ", Counters.Select(pair => $"{pair.Key}={pair.Value:N0}"));
}

/// <summary>
/// Task-shaped operations for the storage cards, implemented in Composition against Core's storage
/// maintenance services.
/// </summary>
public interface IStorageSettings
{
    Task<StorageCleanupEstimate> CreateCleanupPreviewAsync(
        IReadOnlyList<string> targetIds, int? olderThanDays, CancellationToken cancellationToken = default);
    /// <summary>requestId is Core's idempotency key: the same value returns the same job.</summary>
    Task<Guid> CreateCleanupJobAsync(Guid previewId, string requestId, CancellationToken cancellationToken = default);
    Task<StorageCleanupRun?> ReadCleanupJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageCleanupRun>> ListCleanupJobsAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageCleanupEvent>> ReadCleanupEventsAsync(Guid jobId, int limit = 200, CancellationToken cancellationToken = default);
    Task ConfirmCleanupJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CancelCleanupJobAsync(Guid jobId, CancellationToken cancellationToken = default);    Task<StorageSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageTrendPoint>> ReadTrendAsync(int days, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageDataClass>> ListDataClassesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListProtectedObjectsAsync(CancellationToken cancellationToken = default);
    Task<StorageRefreshStatus> RequestRefreshAsync(CancellationToken cancellationToken = default);
    Task<StorageRetentionPolicy> ReadPolicyAsync(CancellationToken cancellationToken = default);
    Task SavePolicyAsync(StorageRetentionPolicyUpdate update, CancellationToken cancellationToken = default);
}

public static class StorageText
{
    /// <summary>The trend windows the card offers; Core accepts any positive day count.</summary>
    public static IReadOnlyList<int> TrendRanges { get; } = [7, 30, 90];

    public const string SnapshotNotice =
        "总占用与分类占用来自 Core 的缓存快照：读取不会触发扫描。要拿新数据必须显式请求刷新，刷新在后台进行。";

    public const string EstimateNotice =
        "分类占用是估算值：Estimating/Unavailable 表示当时没有可用的估算，界面显示为未知而不是 0。";

    public const string ProtectedNotice =
        "受保护对象不能被手动清理；自动清理也只覆盖 AutomaticCleanupAllowed=true 的分类。";

    public const string PolicyRevisionNotice =
        "策略更新携带期望版本（CAS）：版本不一致表示别人先改过，界面必须让你重新读取再改，而不是覆盖别人的修改。";

    public static string FormatBytes(long? bytes) => bytes switch
    {
        null => "未知",
        < 0 => "未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    public static string DescribeSafetyLevel(string? level) => level switch
    {
        null or "" => "安全级别未知",
        var value when string.Equals(value, "Disposable", StringComparison.OrdinalIgnoreCase) => "Disposable（可丢弃）",
        var value when string.Equals(value, "Derived", StringComparison.OrdinalIgnoreCase) => "Derived（派生数据）",
        var value when string.Equals(value, "Evidence", StringComparison.OrdinalIgnoreCase) => "Evidence（证据）",
        var value when string.Equals(value, "UserData", StringComparison.OrdinalIgnoreCase) => "UserData（用户数据）",
        var value => value
    };

    public static string DescribeEstimateState(string? state) => state switch
    {
        null or "" => "未知",
        var value when string.Equals(value, "Estimating", StringComparison.OrdinalIgnoreCase) => "估算中",
        var value when string.Equals(value, "Updated", StringComparison.OrdinalIgnoreCase) => "已更新",
        var value when string.Equals(value, "Unavailable", StringComparison.OrdinalIgnoreCase) => "本次不可用",
        var value => value
    };

    public static string DescribeRefreshState(string? state) => state switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "Idle", StringComparison.OrdinalIgnoreCase) => "空闲",
        var value when string.Equals(value, "Running", StringComparison.OrdinalIgnoreCase) => "刷新中",
        var value when string.Equals(value, "Completed", StringComparison.OrdinalIgnoreCase) => "已完成",
        var value when string.Equals(value, "Failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value => value
    };

    public static string DescribeCleanupPermissions(bool manual, bool automatic) =>
        (manual, automatic) switch
        {
            (true, true) => "可手动清理 · 可自动清理",
            (true, false) => "仅可手动清理",
            (false, true) => "仅可自动清理",
            (false, false) => "不可清理"
        };

    public static string DescribeRetentionRange(int? min, int? max, int? @default)
    {
        if (min is null && max is null) return @default is null ? "未声明允许范围" : $"默认 {(@default)} 天";
        var range = min is null ? $"≤ {max} 天" : max is null ? $"≥ {min} 天" : $"{min}~{max} 天";
        return @default is null ? $"允许 {range}" : $"允许 {range} · 默认 {@default} 天";
    }

    /// <summary>Share of the snapshot total, or null when nothing is measurable (never a fabricated 0%).</summary>
    public static double? ShareOf(long? part, long total) =>
        part is null || total <= 0 ? null : Math.Round(part.Value * 100.0 / total, 1);

    public static string DescribeShare(long? part, long total) =>
        ShareOf(part, total) is { } share ? $"{share:0.#}%" : "占比未知";

    public const string PreviewNotice =
        "预览是 Core 的有界估算并会过期：创建作业必须使用同一个 previewId，过期后要重新预览。";

    public const string JobNotice =
        "清理作业先创建再确认：创建后处于「需要确认」，确认后才会执行；作业由 Core 串行执行，界面只读取进度与事件。";

    public const string BudgetNotice =
        "Core 的清理执行带有每作业行数预算（MemoryJob 的 Budget），但作业 DTO 不暴露预算字段，也没有「超预算继续」的操作；" +
        "界面只呈现进度、剩余估算与作业状态，不提供任何绕过预算的按钮。";

    public static string DescribeJobStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "Queued", StringComparison.OrdinalIgnoreCase) => "排队中",
        var value when string.Equals(value, "Running", StringComparison.OrdinalIgnoreCase) => "执行中",
        var value when string.Equals(value, "PausedBusy", StringComparison.OrdinalIgnoreCase) => "暂缓（维护繁忙）",
        var value when string.Equals(value, "NeedsConfirmation", StringComparison.OrdinalIgnoreCase) => "需要确认",
        var value when string.Equals(value, "Cancelling", StringComparison.OrdinalIgnoreCase) => "取消中",
        var value when string.Equals(value, "Completed", StringComparison.OrdinalIgnoreCase) => "已完成",
        var value when string.Equals(value, "Partial", StringComparison.OrdinalIgnoreCase) => "部分完成",
        var value when string.Equals(value, "Failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value when string.Equals(value, "Cancelled", StringComparison.OrdinalIgnoreCase) => "已取消",
        var value => value
    };

    public static string DescribeEventKind(string? kind) => kind switch
    {
        null or "" => "事件",
        var value when string.Equals(value, "started", StringComparison.OrdinalIgnoreCase) => "开始",
        var value when string.Equals(value, "progress", StringComparison.OrdinalIgnoreCase) => "进度",
        var value when string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase) => "完成",
        var value when string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value when string.Equals(value, "cancelled", StringComparison.OrdinalIgnoreCase) => "已取消",
        var value when string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase) => "警告",
        var value => value
    };

    /// <summary>Preview needs at least one target; days and cutoff are mutually exclusive in Core.</summary>
    public static IReadOnlyList<string> ValidatePreview(IReadOnlyList<string> targetIds, string? olderThanDays)
    {
        var errors = new List<string>();
        if (targetIds.Count == 0) errors.Add("至少要选择一个数据类别。");
        if (!string.IsNullOrWhiteSpace(olderThanDays))
        {
            // VoiceSettingsText.ParseOptionalInt maps 0 to null, which would misreport "0" as non-numeric.
            if (!int.TryParse(olderThanDays.Trim(), out var parsed)) errors.Add("「早于天数」必须是整数。");
            else if (parsed <= 0) errors.Add("「早于天数」必须大于 0。");
        }
        return errors;
    }

    public static IReadOnlyList<string> Validate(StorageRetentionPolicyUpdate update)
    {
        var errors = new List<string>();
        if (update.ExpectedRevision < 0) errors.Add("期望策略版本无效。");
        if (update.Targets.Count == 0) errors.Add("至少要提交一个目标分类。");
        foreach (var target in update.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.TargetId)) errors.Add("目标分类 ID 不能为空。");
            // Core rejects 0 outright: disabling must be expressed with Enabled=false.
            if (target.RetentionDays is <= 0) errors.Add($"分类 {target.TargetId} 的保留天数必须大于 0（停用请用「未启用」）。");
        }
        return errors;
    }

    /// <summary>A target edit outside the declared range is caught before Core has to reject it.</summary>
    public static IReadOnlyList<string> ValidateTarget(StorageRetentionTarget target, bool enabled, int? retentionDays)
    {
        var errors = new List<string>();
        if (!enabled || retentionDays is null) return errors;
        if (retentionDays <= 0) { errors.Add("保留天数必须大于 0。"); return errors; }
        if (target.MinRetentionDays is { } min && retentionDays < min) errors.Add($"不能小于最小值 {min} 天。");
        if (target.MaxRetentionDays is { } max && retentionDays > max) errors.Add($"不能大于最大值 {max} 天。");
        return errors;
    }
}
