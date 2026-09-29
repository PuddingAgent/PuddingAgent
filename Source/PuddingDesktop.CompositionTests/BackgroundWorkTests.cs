using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Core;

namespace PuddingDesktop.CompositionTests;

/// <summary>
/// 统一后台作业入口。它存在的理由是三条被重复实现的保证，所以测试就钉这三条：
/// 启动不等待（后台重活不阻塞宿主）、跑在低优先级线程上（不与首屏/交互抢 CPU）、
/// 停止送取消且有上界（不把停止路径挂死）。另外钉住"作业失败不影响停止"。
/// </summary>
public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task StartReturnsImmediatelyWhileTheWorkIsStillRunning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = BackgroundWork.Start("test.blocking", async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, NullLogger.Instance);

        // 确定性判据：先等作业确实在跑，再断言"它还没完成时 Start 已经返回"。
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(work.IsRunning, "作业仍在跑时 Start 就必须已经返回");
        Assert.False(work.Completion.IsCompleted);

        await work.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkRunsOnABelowNormalPriorityThread()
    {
        var observed = new TaskCompletionSource<(ThreadPriority Priority, string? Name)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var work = BackgroundWork.Start("test.priority", ct =>
        {
            observed.TrySetResult((Thread.CurrentThread.Priority, Thread.CurrentThread.Name));
            return Task.CompletedTask;
        }, NullLogger.Instance);

        var (priority, name) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ThreadPriority.BelowNormal, priority);
        Assert.Equal("pudding-bg-test.priority", name);
        await work.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopCancelsInFlightWorkAndConverges()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = BackgroundWork.Start("test.cancel", async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }, NullLogger.Instance);

        await work.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompleted, "停止必须把取消送到在途作业");
    }

    [Fact]
    public async Task StopIsBoundedEvenIfTheWorkIgnoresCancellation()
    {
        var work = BackgroundWork.Start(
            "test.stubborn", _ => new TaskCompletionSource().Task, NullLogger.Instance);

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        await work.StopAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(3),
            "作业无视取消时停止仍必须有上界");
    }

    [Fact]
    public async Task FailingWorkDoesNotBreakTheStopPath()
    {
        var work = BackgroundWork.Start(
            "test.failing", _ => throw new InvalidOperationException("boom"), NullLogger.Instance);

        await work.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        await work.StopAsync(TimeSpan.FromSeconds(5));
        await work.StopAsync(TimeSpan.FromSeconds(5));   // 幂等
    }
}
