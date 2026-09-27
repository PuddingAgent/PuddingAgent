using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-09 inventory and policy slice: sizes, shares, protections and CAS validation.</summary>
public sealed class StorageContractTests
{
    [Fact]
    public void ByteFormattingAndSharesNeverFabricateNumbers()
    {
        Assert.Equal("未知", StorageText.FormatBytes(null));
        Assert.Equal("未知", StorageText.FormatBytes(-1));
        Assert.Equal("512 B", StorageText.FormatBytes(512));
        Assert.Equal("1.5 KB", StorageText.FormatBytes(1536));
        Assert.Equal("2.5 MB", StorageText.FormatBytes(2_621_440));
        Assert.Equal("1.5 GB", StorageText.FormatBytes(1_610_612_736));

        Assert.Equal(25d, StorageText.ShareOf(25, 100));
        Assert.Equal("25%", StorageText.DescribeShare(25, 100));
        // 没有可测总量时不编一个 0%。
        Assert.Null(StorageText.ShareOf(10, 0));
        Assert.Null(StorageText.ShareOf(null, 100));
        Assert.Equal("占比未知", StorageText.DescribeShare(10, 0));
    }

    [Fact]
    public void VocabularyIsTranslatedButUnknownValuesPassThrough()
    {
        Assert.Contains("可丢弃", StorageText.DescribeSafetyLevel("Disposable"), StringComparison.Ordinal);
        Assert.Contains("用户数据", StorageText.DescribeSafetyLevel("UserData"), StringComparison.Ordinal);
        Assert.Equal("SomethingNew", StorageText.DescribeSafetyLevel("SomethingNew"));
        Assert.Equal("安全级别未知", StorageText.DescribeSafetyLevel(null));

        Assert.Equal("估算中", StorageText.DescribeEstimateState("Estimating"));
        Assert.Equal("本次不可用", StorageText.DescribeEstimateState("Unavailable"));
        Assert.Equal("状态未知", StorageText.DescribeRefreshState(null));
        Assert.Equal("刷新中", StorageText.DescribeRefreshState("running"));

        Assert.Equal("可手动清理 · 可自动清理", StorageText.DescribeCleanupPermissions(true, true));
        Assert.Equal("不可清理", StorageText.DescribeCleanupPermissions(false, false));
        Assert.Equal([7, 30, 90], StorageText.TrendRanges);
    }

    [Fact]
    public void RetentionRangesAreDescribedWithTheRealBounds()
    {
        Assert.Equal("允许 7~30 天 · 默认 14 天", StorageText.DescribeRetentionRange(7, 30, 14));
        Assert.Equal("允许 ≤ 30 天", StorageText.DescribeRetentionRange(null, 30, null));
        Assert.Equal("允许 ≥ 7 天", StorageText.DescribeRetentionRange(7, null, null));
        Assert.Equal("默认 14 天", StorageText.DescribeRetentionRange(null, null, 14));
        Assert.Equal("未声明允许范围", StorageText.DescribeRetentionRange(null, null, null));

        var target = new StorageRetentionTarget("cache", "Cache", true, 10, true, 14, 7, 30);
        Assert.Equal("启用 · 保留 10 天", target.EffectiveText);
        Assert.Equal("未启用", (target with { Enabled = false }).EffectiveText);
        Assert.Equal("启用（未设置天数）", (target with { RetentionDays = null }).EffectiveText);
    }

    [Fact]
    public void PolicyValidationCatchesWhatCoreWouldReject()
    {
        var update = new StorageRetentionPolicyUpdate(3, true,
            [new StorageRetentionTargetEdit("cache", true, 14)]);
        Assert.Empty(StorageText.Validate(update));
        Assert.Contains("目标分类", StorageText.Validate(update with { Targets = [] }).Single(), StringComparison.Ordinal);
        // Core 明确拒绝 0：停用必须用 Enabled=false。
        var zero = update with { Targets = [new StorageRetentionTargetEdit("cache", true, 0)] };
        Assert.Contains("必须大于 0", StorageText.Validate(zero).Single(), StringComparison.Ordinal);
        var disabled = update with { Targets = [new StorageRetentionTargetEdit("cache", false, null)] };
        Assert.Empty(StorageText.Validate(disabled));

        var target = new StorageRetentionTarget("cache", "Cache", false, null, true, 14, 7, 30);
        Assert.Empty(StorageText.ValidateTarget(target, false, null));
        Assert.Contains("不能小于最小值", StorageText.ValidateTarget(target, true, 3).Single(), StringComparison.Ordinal);
        Assert.Contains("不能大于最大值", StorageText.ValidateTarget(target, true, 60).Single(), StringComparison.Ordinal);
        Assert.Empty(StorageText.ValidateTarget(target, true, 7));
    }

    [Fact]
    public void CleanupJobStatusesAndEventsAreDescribedVerbatim()
    {
        Assert.Equal("需要确认", StorageText.DescribeJobStatus("NeedsConfirmation"));
        Assert.Equal("部分完成", StorageText.DescribeJobStatus("Partial"));
        Assert.Equal("取消中", StorageText.DescribeJobStatus("cancelling"));
        Assert.Equal("状态未知", StorageText.DescribeJobStatus(null));
        Assert.Equal("SomethingNew", StorageText.DescribeJobStatus("SomethingNew"));
        Assert.Equal("进度", StorageText.DescribeEventKind("progress"));
        Assert.Equal("事件", StorageText.DescribeEventKind(""));

        var progress = new StorageCleanupCounters(100, 40, 30, 5, 3, 2, 7, 4096, 60);
        Assert.Contains("已处理 40/100", progress.SummaryText, StringComparison.Ordinal);
        Assert.Contains("剩余约 60 行", progress.RemainingText, StringComparison.Ordinal);
        Assert.Equal("剩余未知", (progress with { RemainingRowsEstimate = null }).RemainingText);
    }

    [Fact]
    public void CleanupJobHelpersSayWhatCanBeDone()
    {
        var job = new StorageCleanupRun(Guid.NewGuid(), "manual", "NeedsConfirmation", DateTimeOffset.UtcNow,
            null, null, DateTimeOffset.UtcNow, ["cache"], new StorageCleanupCounters(0, 0, 0, 0, 0, 0, 0, 0, null),
            [], "", "");
        Assert.True(job.NeedsConfirmation);
        Assert.False(job.IsTerminal);
        Assert.True(job.CanCancel);

        Assert.True((job with { Status = "Running" }).CanCancel);
        Assert.False((job with { Status = "Cancelling" }).CanCancel);
        Assert.True((job with { Status = "Completed" }).IsTerminal);
        Assert.False((job with { Status = "Completed" }).CanCancel);
        Assert.True((job with { Status = "Failed" }).IsTerminal);
    }

    [Fact]
    public void PreviewValidationAndExpiryAreExplicit()
    {
        Assert.Empty(StorageText.ValidatePreview(["cache"], "30"));
        Assert.Empty(StorageText.ValidatePreview(["cache"], ""));
        Assert.Contains("数据类别", StorageText.ValidatePreview([], "30").Single(), StringComparison.Ordinal);
        Assert.Contains("整数", StorageText.ValidatePreview(["cache"], "abc").Single(), StringComparison.Ordinal);
        Assert.Contains("大于 0", StorageText.ValidatePreview(["cache"], "0").Single(), StringComparison.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var preview = new StorageCleanupEstimate(Guid.NewGuid(), 1, 2, now, now.AddMinutes(5), now, true, [],
            [new StorageCleanupTargetEstimate("cache", "Cache", "delete rows", 10, false, 2048, now)]);
        Assert.False(preview.IsExpired(now));
        Assert.True(preview.IsExpired(now.AddMinutes(6)));
        Assert.Equal("10 行", preview.Targets[0].CandidatesText);
        Assert.Contains("实际可能更多", (preview.Targets[0] with { CandidatesTruncated = true }).CandidatesText, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetGapIsStatedRatherThanHidden()
    {
        // Core 有预算字段与内部停止条件，但没有「超预算继续」操作，作业 DTO 也不暴露预算。
        Assert.Contains("不暴露预算字段", StorageText.BudgetNotice, StringComparison.Ordinal);
        Assert.Contains("没有「超预算继续」", StorageText.BudgetNotice, StringComparison.Ordinal);
        Assert.Contains("会过期", StorageText.PreviewNotice, StringComparison.Ordinal);
        Assert.Contains("先创建再确认", StorageText.JobNotice, StringComparison.Ordinal);
    }
    [Fact]
    public void SnapshotTotalsSplitDatabasesFromClasses()
    {
        var snapshot = new StorageSnapshot(Guid.NewGuid(), 4, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            false, [],
            [new StorageDatabase("main", "Main", "main.db", 2048, 1024, 512, 512, 256)],
            [new StorageInventoryClass("cache", "Cache", 1024, 10, null, null, "Updated", DateTimeOffset.UtcNow)]);
        Assert.Equal(2048, snapshot.DatabaseBytes);
        Assert.Equal(1024, snapshot.ClassBytes);
        Assert.Equal(3072, snapshot.TotalBytes);
        Assert.False(snapshot.IsEmpty);

        // 估算不可用时显示未知而不是 0。
        var unknown = new StorageInventoryClass("cache", "Cache", null, null, null, null, "Unavailable", null);
        Assert.Equal("未知", unknown.SizeText);
        Assert.Equal("行数未知", unknown.RowsText);
        Assert.Equal("本次不可用", unknown.EstimateText);

        var refresh = new StorageRefreshStatus(Guid.NewGuid(), "Running", DateTimeOffset.UtcNow, null, 9);
        Assert.Equal("刷新中", refresh.StateText);
        Assert.Contains("不会触发扫描", StorageText.SnapshotNotice, StringComparison.Ordinal);
        Assert.Contains("CAS", StorageText.PolicyRevisionNotice, StringComparison.Ordinal);
    }
}