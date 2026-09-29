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
}
