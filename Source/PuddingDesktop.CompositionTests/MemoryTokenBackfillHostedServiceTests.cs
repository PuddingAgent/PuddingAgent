using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingHost.Services;

namespace PuddingDesktop.CompositionTests;

/// <summary>
/// jieba 词元回填是"结果可等、就绪不必等"的重活（实测热态 577 ms）：它必须在后台跑，
/// 停止时把取消送到在途任务，且任务不收敛时停止仍有上界。
/// </summary>
public sealed class MemoryTokenBackfillHostedServiceTests
{
    [Fact]
    public async Task StartDoesNotWaitForTheBackfill()
    {
        var backfill = new CancelAwareBackfill();
        var service = new MemoryTokenBackfillHostedService(backfill, NullLogger<MemoryTokenBackfillHostedService>.Instance);

        // 判据用"内部任务是否仍未完成"，不用墙钟阈值：后者在负载下会抖。
        // 先等它确实被调度（Task.Run 不保证 StartAsync 返回时就已启动），再断言"尚未完成"。
        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await backfill.StartedSignal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(backfill.Finished, "回填尚未完成时 StartAsync 就必须已经返回");

        await service.StopAsync(CancellationToken.None);
        Assert.True(backfill.Cancelled, "停止必须把取消送到在途回填");
    }

    [Fact]
    public async Task StopConvergesWithinItsBoundEvenIfTheBackfillNeverFinishes()
    {
        var service = new MemoryTokenBackfillHostedService(
            new NeverFinishingBackfill(), NullLogger<MemoryTokenBackfillHostedService>.Instance)
        {
            StopConvergenceTimeout = TimeSpan.FromMilliseconds(200),
        };
        await service.StartAsync(CancellationToken.None);

        var startedAt = Stopwatch.GetTimestamp();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(3),
            "回填不收敛时停止仍必须有上界");
    }

    [Fact]
    public async Task FailingBackfillDoesNotBreakStartupOrStop()
    {
        var service = new MemoryTokenBackfillHostedService(
            new FailingBackfill(), NullLogger<MemoryTokenBackfillHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopIsIdempotent()
    {
        var service = new MemoryTokenBackfillHostedService(
            new CancelAwareBackfill(), NullLogger<MemoryTokenBackfillHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class CancelAwareBackfill : IMemoryTokenBackfill
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _finished;
        public bool Cancelled { get; private set; }

        /// <summary>回填已开始执行的信号（Task.Run 不保证 StartAsync 返回时就已启动）。</summary>
        public Task StartedSignal => _started.Task;

        /// <summary>回填是否已经结束。用它当判据，避免"墙钟 &lt; N ms"这种在负载下会抖的断言。</summary>
        public bool Finished => _finished;

        public async Task RunAsync(CancellationToken ct)
        {
            _started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            finally { _finished = true; }
        }
    }

    private sealed class NeverFinishingBackfill : IMemoryTokenBackfill
    {
        public Task RunAsync(CancellationToken ct) => new TaskCompletionSource().Task;
    }

    private sealed class FailingBackfill : IMemoryTokenBackfill
    {
        public Task RunAsync(CancellationToken ct) => throw new InvalidOperationException("backfill failed");
    }
}
