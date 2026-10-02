using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Runtime;

public sealed class CoreProcessMetricsSamplerTests
{
    [Fact]
    public void CpuUsesMachineCapacityAndResetsOnPidReuse()
    {
        var tracker = new CoreCpuUsageTracker();
        var start = DateTimeOffset.UtcNow;
        Assert.Null(tracker.Observe(42, start, TimeSpan.Zero, TimeSpan.Zero, 8));
        Assert.Equal(25d, tracker.Observe(42, start, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), 8));
        Assert.Null(tracker.Observe(42, start.AddMinutes(1), TimeSpan.Zero, TimeSpan.FromSeconds(3), 8));
        tracker.Reset();
        Assert.Null(tracker.Observe(42, start, TimeSpan.Zero, TimeSpan.FromSeconds(4), 8));
    }

    [Fact]
    public void InvalidIntervalsDoNotProduceFakeCpuValues()
    {
        var tracker = new CoreCpuUsageTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(1, start, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 1);
        Assert.Null(tracker.Observe(1, start, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), 1));
        Assert.Null(tracker.Observe(1, start, TimeSpan.Zero, TimeSpan.FromSeconds(3), 1));
        Assert.Null(tracker.Observe(2, start, TimeSpan.Zero, TimeSpan.FromSeconds(4), 1));
    }

    [Fact]
    public void SamplesActualProcessAndClearsUnavailableMetrics()
    {
        var sampler = new CoreProcessMetricsSampler();
        var metrics = sampler.Sample(Environment.ProcessId);
        Assert.NotNull(metrics);
        Assert.True(metrics.WorkingSetBytes > 0);
        Assert.True(metrics.Uptime >= TimeSpan.Zero);
        Assert.Null(metrics.CpuPercent);
        Assert.Null(sampler.Sample(null));
        Assert.Null(sampler.Sample(int.MaxValue));
    }

    /// <summary>
    /// 面板必须能给出任务管理器口径的专用工作集，而不是把总工作集当成进程占用：
    /// 专用工作集是总工作集的子集，二者相等说明采样退回到了工作集口径（界面会标注）。
    /// </summary>
    [Fact]
    public void PrivateWorkingSetIsReportedAndNeverExceedsTotalWorkingSet()
    {
        var sampler = new CoreProcessMetricsSampler();
        var metrics = sampler.Sample(Environment.ProcessId);
        Assert.NotNull(metrics);
        if (!Environment.Is64BitProcess) return;
        Assert.NotNull(metrics.PrivateWorkingSetBytes);
        Assert.True(metrics.PrivateWorkingSetBytes > 0);
        Assert.True(metrics.PrivateWorkingSetBytes <= metrics.WorkingSetBytes);
    }

    [Fact]
    public void MemoryCardShowsPrivateWorkingSetAndDisclosesWorkingSet()
    {
        var started = DateTimeOffset.UtcNow;
        var metrics = new CoreProcessMetrics(started, TimeSpan.FromMinutes(5), 1007L * 1048576, 733L * 1048576, 4.4);

        var (value, detail) = CoreMemoryDisplay.Format(metrics);

        Assert.Equal("733 MiB", value);
        Assert.Contains("任务管理器口径", detail);
        Assert.Contains("工作集 1007 MiB", detail);
    }

    [Fact]
    public void MemoryCardFallsBackToWorkingSetOnlyWhenPrivateWorkingSetIsUnavailable()
    {
        var metrics = new CoreProcessMetrics(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), 1007L * 1048576, null, null);

        var (value, detail) = CoreMemoryDisplay.Format(metrics);

        Assert.Equal("1007 MiB", value);
        Assert.Equal(CoreMemoryDisplay.WorkingSetFallbackDetail, detail);
        Assert.Contains("含共享页", detail);

        var (unknown, idleDetail) = CoreMemoryDisplay.Format(null);
        Assert.Equal("—", unknown);
        Assert.Equal(CoreMemoryDisplay.IdleDetail, idleDetail);
    }
}
