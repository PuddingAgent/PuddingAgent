using System;
using System.Collections.Generic;
using System.Linq;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// `StartupPhaseTracker` 的确定性测试：时钟与输出通道全部注入，不依赖真实时间、不写文件。
/// 目的（2026-09-30）：让「启动各阶段各花多久」这件事有**可失败**的断言保护 ——
/// 埋点被改坏（丢字段、算错增量、时钟回拨产生负值、非法阶段名混入）时这里必须变红。
/// </summary>
public sealed class StartupPhaseTrackerTests
{
    [Fact]
    public void Marks_RecordTotalAndDelta_DerivedFromInjectedClock()
    {
        var ticks = new Queue<long>(new long[] { 0, 100, 250 });
        var tracker = new StartupPhaseTracker(() => ticks.Dequeue());

        tracker.Mark(StartupPhases.ProcessStart);
        tracker.Mark(StartupPhases.OptionsResolved);
        tracker.Mark(StartupPhases.DataRootLease);

        Assert.Equal(
            new[] { StartupPhases.ProcessStart, StartupPhases.OptionsResolved, StartupPhases.DataRootLease },
            tracker.Marks.Select(m => m.Phase).ToArray());
        Assert.Equal(new long[] { 0, 100, 250 }, tracker.Marks.Select(m => m.TotalMs).ToArray());
        Assert.Equal(new long[] { 0, 100, 150 }, tracker.Marks.Select(m => m.DeltaMs).ToArray());
    }

    [Fact]
    public void Mark_WritesExactlyOneLinePerMark_InOrder_ThroughTheSink()
    {
        var ticks = new Queue<long>(new long[] { 0, 17_500, 21_500 });
        var lines = new List<string>();
        var tracker = new StartupPhaseTracker(() => ticks.Dequeue(), lines.Add);

        tracker.Mark(StartupPhases.ProcessStart);
        tracker.Mark(StartupPhases.LoggingReady);
        tracker.Mark(StartupPhases.Ready);

        Assert.Equal(
            new[]
            {
                "[StartupPhase] process-start total=0ms delta=0ms",
                "[StartupPhase] logging-ready total=17500ms delta=17500ms",
                "[StartupPhase] ready total=21500ms delta=4000ms",
            },
            lines.ToArray());
    }

    [Fact]
    public void Mark_ClampsNegativeDelta_WhenClockMovesBackwards()
    {
        var ticks = new Queue<long>(new long[] { 500, 100 });
        var tracker = new StartupPhaseTracker(() => ticks.Dequeue());

        tracker.Mark(StartupPhases.ProcessStart);
        var second = tracker.Mark(StartupPhases.OptionsResolved);

        Assert.Equal(500, second.TotalMs);
        Assert.Equal(0, second.DeltaMs);
    }

    [Fact]
    public void Mark_RejectsBlankPhaseName_WithoutLeavingAMark()
    {
        var tracker = new StartupPhaseTracker(() => 0);

        Assert.Throws<ArgumentException>(() => tracker.Mark("   "));
        Assert.Empty(tracker.Marks);
    }
}
