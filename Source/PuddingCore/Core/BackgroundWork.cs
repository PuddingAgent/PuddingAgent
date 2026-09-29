using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PuddingCode.Core;

/// <summary>
/// 后台重活的统一入口（**低层**：Core 层，Platform 与 Host 都可用）。
/// <para>
/// 为什么要有它：MCP 首次对账（实测 2.15 s）、jieba 词元回填（577-720 ms）以及后续同类重活，
/// 需要同一组保证 —— **不阻塞宿主启动**、跑在**低优先级**线程上（不与首屏/交互抢 CPU）、
/// 停止时把取消送到在途任务并按**上界收敛**、完成/失败都留**带耗时的日志**。
/// 这三条在 MCP 与 jieba 上已各自实现过一次（同形代码×2），第三处出现之前先抽出来。
/// </para>
/// <para>
/// **不做的事**：它不注册"我还没就绪"的状态面。调用方若把结果暴露给用户（例如记忆检索依赖词元、
/// MCP 工具清单依赖连接），必须自己把"预热中"表达清楚 —— 后台化不等于可以静默降级。
/// </para>
/// </summary>
public sealed class BackgroundWork
{
    private readonly string _name;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _stopped;

    private BackgroundWork(string name, Func<CancellationToken, Task> body, ILogger logger)
    {
        _name = name;
        _logger = logger;
        var startedAt = Stopwatch.GetTimestamp();
        _thread = new Thread(() =>
        {
            try
            {
                body(_stopping.Token).GetAwaiter().GetResult();
                _logger.LogInformation(
                    "[BackgroundWork:{Name}] completed in {ElapsedMs:N0} ms (background, thread={ThreadName})",
                    name, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, Thread.CurrentThread.Name);
                _completion.TrySetResult();
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                _logger.LogInformation("[BackgroundWork:{Name}] cancelled by host shutdown", name);
                _completion.TrySetCanceled(_stopping.Token);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "[BackgroundWork:{Name}] failed (background; caller decides whether to retry)", name);
                _completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            // 低优先级：后台重活不得与首屏渲染、用户交互抢 CPU（用户 2026-09-29 的方向）。
            Priority = ThreadPriority.BelowNormal,
            Name = "pudding-bg-" + name,
        };
        _thread.Start();
    }

    /// <summary>启动一个后台作业。**立即返回**，绝不等待作业完成。</summary>
    public static BackgroundWork Start(string name, Func<CancellationToken, Task> body, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(logger);
        return new BackgroundWork(name, body, logger);
    }

    /// <summary>作业的完成（含取消/失败）。停止路径用它做有界收敛。</summary>
    public Task Completion => _completion.Task;

    /// <summary>作业线程是否还在跑。</summary>
    public bool IsRunning => !_completion.Task.IsCompleted;

    /// <summary>
    /// 送取消并按 <paramref name="timeout"/> 有界等待收敛。超时只记 Warning —— 绝不把停止路径挂死。
    /// 幂等；作业失败或取消都不会把异常抛给停止路径。
    /// </summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await _completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "[BackgroundWork:{Name}] did not converge within {TimeoutMs:N0} ms; continuing shutdown",
                    _name, timeout.TotalMilliseconds);
            }
            catch (OperationCanceledException) { /* 作业已取消（或宿主正在停止）：停止路径不关心 */ }
            catch (Exception) { /* 作业失败：已记日志，停止路径不重复抛出 */ }
        }
        finally { _stopping.Dispose(); }
    }
}
