using System.Collections.Concurrent;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// **终态之后**的可选上下文维护调度（ADR-095 D1/D2 的收口，T5 接缝）。
/// <para>
/// 现状问题：`TrimHistoryAsync`（含持久压缩）在 `done` 帧与结果返回**之前**同步执行，
/// 且整个 Turn 持有 <see cref="ISessionExecutionGate"/> 租约 —— 维护耗时直接抬高了用户看到的终态时刻
/// （诊断 2026-10-07 记录的最坏情况是终态前 20.760 s / 22.854 s 的同步压缩）。
/// </para>
/// <para>
/// 本调度器把可选维护挪到终态之后，并满足三条约束：
/// <list type="number">
/// <item>
/// <b>租约释放后</b>：任务先 `EnterAsync` 探一次门（Turn 持有租约到方法返回，因此这一步天然等价于
/// 「等本轮前台租约释放」），随即**立刻释放** —— 维护本身不长期占锁，避免违反 D2
/// 「软维护不得阻塞安全新消息」。真正与其它压缩互斥由 c<CompactionCoordinator> 单飞锁保证。
/// </item>
/// <item>
/// <b>去重 + 抢占</b>：同一会话只保留一个在途请求；更新的请求**抢占**旧的（协作取消），
/// 因为新请求代表更新的历史状态，旧维护的结果必然过期。
/// </item>
/// <item>
/// <b>失败有界</b>：维护是尽力而为，失败只记日志，绝不冒泡影响已提交的 Turn 结果。
/// </item>
/// </list>
/// </para>
/// </summary>
internal sealed class PostTurnMaintenanceScheduler
{
    private readonly ISessionExecutionGate _gate;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly ConcurrentDictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);

    internal PostTurnMaintenanceScheduler(
        ISessionExecutionGate gate,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>在途维护请求数（测试与诊断用）。</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <summary>
    /// 调度一次终态后的维护。
    /// </summary>
    /// <returns>true = 已排队（可能抢占了一个更早的请求）；false = 已有完全相同的在途请求被合并。</returns>
    internal bool Schedule(string sessionId, string reason, Func<CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(work);

        var entry = new InFlight(reason, work);

        // 「最新者胜」：抢占更早的在途请求（它的历史状态已被本次取代）。
        if (_inFlight.TryGetValue(sessionId, out var previous))
        {
            previous.Cancel();
            _logger.LogInformation(
                "[PostTurnMaintenance] Preempting earlier maintenance session={Session} previousReason={Previous} reason={Reason}",
                sessionId, previous.Reason, reason);
        }

        if (!_inFlight.TryAdd(sessionId, entry) && !_inFlight.TryUpdate(sessionId, entry, previous!))
            _inFlight[sessionId] = entry;

        _ = RunAsync(sessionId, entry);
        return true;
    }

    /// <summary>等待所有在途维护结束（测试用；生产不调用）。</summary>
    internal async Task DrainAsync(CancellationToken ct = default)
    {
        while (_inFlight.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var pending = _inFlight.Values.Select(v => v.Completion).ToArray();
            if (pending.Length == 0)
                return;
            await Task.WhenAll(pending).WaitAsync(ct);
        }
    }

    private async Task RunAsync(string sessionId, InFlight entry)
    {
        try
        {
            if (entry.Token.IsCancellationRequested)
                return;

            // ① 等本轮前台租约释放：Turn 持有租约到方法返回，因此这一步就是「终态提交之后」。
            //    立刻释放，不长期占锁（D2：维护不得阻塞新消息）。
            await using (await _gate.EnterAsync(sessionId, "post_turn_maintenance", entry.Token))
            {
            }

            entry.Token.ThrowIfCancellationRequested();

            // ② 真正干活：与其它压缩的互斥由 ContextCompactionService 的 per-session 单飞锁承担。
            await entry.Work(entry.Token);
            _logger.LogInformation(
                "[PostTurnMaintenance] Completed session={Session} reason={Reason}",
                sessionId, entry.Reason);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "[PostTurnMaintenance] Cancelled (superseded) session={Session} reason={Reason}",
                sessionId, entry.Reason);
        }
        catch (Exception ex)
        {
            // 维护失败不得影响已提交的 Turn 结果。
            _logger.LogWarning(
                ex,
                "[PostTurnMaintenance] Failed session={Session} reason={Reason}",
                sessionId, entry.Reason);
        }
        finally
        {
            entry.Complete();
            if (_inFlight.TryGetValue(sessionId, out var current) && ReferenceEquals(current, entry))
                _inFlight.TryRemove(sessionId, out _);
        }
    }

    private sealed class InFlight(string reason, Func<CancellationToken, Task> work)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Reason { get; } = reason;

        public Func<CancellationToken, Task> Work { get; } = work;

        public CancellationToken Token => _cts.Token;

        public Task Completion => _completion.Task;

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已完成；忽略。
            }
        }

        public void Complete() => _completion.TrySetResult();
    }
}
