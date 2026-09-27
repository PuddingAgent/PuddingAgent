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

/// <summary>
/// Task-shaped operations for the storage cards, implemented in Composition against Core's storage
/// maintenance services.
/// </summary>
public interface IStorageSettings
{
    Task<StorageSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
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
