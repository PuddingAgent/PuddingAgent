using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingHost.Services;

/// <summary>
/// jieba 词元回填的窄端口：宿主只表达"回填一次"，实现细节（走哪个库、要不要分页）留在实施类里。
/// 这样 hosted service 可以用一个几行的假实现测出"不阻塞启动 / 停止会取消 / 失败有上界"。
/// </summary>
public interface IMemoryTokenBackfill
{
    Task RunAsync(CancellationToken ct);
}

/// <summary>生产实现：直接调 <see cref="IMemoryLibrary.BackfillTokensAsync"/>（单例）。</summary>
public sealed class MemoryLibraryTokenBackfill(IMemoryLibrary library) : IMemoryTokenBackfill
{
    public Task RunAsync(CancellationToken ct) => library.BackfillTokensAsync(ct);
}

/// <summary>
/// jieba 词元回填的**后台**执行者。它原先在 <c>PuddingApplicationInitializer</c> 里被同步 await
/// （实测热态 577 ms，冷态更多），但"内核就绪"并不依赖它：词元只影响记忆检索的召回质量。
/// <para>
/// **诚实边界**：回填完成前，依赖词元的记忆检索可能召回不全 ⇒ 界面应显示"索引预热中"，
/// 而不是把"暂时搜不到"当成"没有数据"。这条 UI 提示尚未实现。
/// </para>
/// </summary>
public sealed class MemoryTokenBackfillHostedService(
    IMemoryTokenBackfill backfill,
    ILogger<MemoryTokenBackfillHostedService> logger) : IHostedService
{
    /// <summary>停止时等待在途回填收敛的上界；超时只记 Warning，不把停止路径挂死。</summary>
    public static readonly TimeSpan DefaultStopConvergenceTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;
    private int _stopped;

    /// <summary>等待在途回填收敛的上界（可调，测试用）。</summary>
    public TimeSpan StopConvergenceTimeout { get; init; } = DefaultStopConvergenceTimeout;

    /// <summary>测试接缝：在途回填任务。</summary>
    internal Task? Run => _run;

    public Task StartAsync(CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _run = Task.Run(async () =>
        {
            var token = _stopping.Token;
            try
            {
                await backfill.RunAsync(token).ConfigureAwait(false);
                logger.LogInformation(
                    "[MemoryTokenBackfill] completed in {ElapsedMs:N0} ms (background).",
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                logger.LogInformation("[MemoryTokenBackfill] cancelled by host shutdown.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[MemoryTokenBackfill] failed (background; memory recall may stay incomplete).");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            if (_run is not { } run) return;
            try
            {
                await run.WaitAsync(StopConvergenceTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning(
                    "[MemoryTokenBackfill] did not converge within {TimeoutMs:N0} ms; continuing shutdown.",
                    StopConvergenceTimeout.TotalMilliseconds);
            }
            catch (OperationCanceledException) { /* 宿主正在停止：在途任务已收到取消 */ }
        }
        finally { _stopping.Dispose(); }
    }
}
