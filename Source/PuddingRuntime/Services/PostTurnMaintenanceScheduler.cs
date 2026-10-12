using System.Collections.Concurrent;
using PuddingCode.Runtime;
using PuddingRuntime.Services.Messaging;

namespace PuddingRuntime.Services;

/// <summary>
/// **终态之后**的可选上下文维护调度（ADR-095 D1/D2 的收口，T5 接缝；R2 加固）。
/// <para>
/// 现状问题：`TrimHistoryAsync`（含持久压缩）在 `done` 帧与结果返回**之前**同步执行，
/// 且整个 Turn 持有 <see cref="ISessionExecutionGate"/> 租约 —— 维护耗时直接抬高了用户看到的终态时刻
/// （诊断 2026-10-07 记录的最坏情况是终态前 20.760 s / 22.854 s 的同步压缩）。
/// </para>
/// <para>
/// R2 加固要点（复核 §3）：
/// <list type="number">
/// <item>
/// <b>接入前台准入</b>：任务按 (workspace, session) 注册为**会话级后台工作**
/// （<see cref="AgentExecutionAdmissionCoordinator.TryRegisterSessionBackground"/>），
/// 注册用的 CTS 就是本任务的 token ⇒ 新消息受理时的
/// <c>ReserveSessionForeground</c> / 前台 <c>AcquireSessionForeground</c> 会**立刻取消**它，
/// 不必等下一次 <c>Schedule</c>。
/// </item>
/// <item>
/// <b>启动前双重校验</b>：先注册（无前台需求才成功），再探一次执行门（终态租约已释放），
/// 然后**再次**确认没有前台需求才开始干活。
/// </item>
/// <item>
/// <b>同候选合并</b>：同一候选键（reason + candidateKey）已在途 ⇒ 合并并返回 false；
/// 不同候选才是「最新者胜」抢占。
/// </item>
/// <item>
/// <b>原子移除</b>：旧的 finally 只能移除**它自己**那一项（按键值对原子移除），
/// 绝不会误删后来者。
/// </item>
/// <item>
/// <b>停机与超时</b>：<see cref="StopAsync"/> 取消并等待在途任务；单项维护有 deadline，
/// 超时按取消处理；未退出的任务保留可诊断状态（<see cref="DescribeInFlight"/>）。
/// </item>
/// </list>
/// </para>
/// </summary>
internal sealed class PostTurnMaintenanceScheduler
{
    /// <summary>单项维护的默认 deadline（超时即协作取消；维护是可选能力）。</summary>
    internal static readonly TimeSpan DefaultMaintenanceTimeout = TimeSpan.FromMinutes(5);

    private readonly ISessionExecutionGate _gate;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly AgentExecutionAdmissionCoordinator? _admission;
    private readonly TimeSpan _maintenanceTimeout;
    private readonly ConcurrentDictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private int _stopping;

    internal PostTurnMaintenanceScheduler(
        ISessionExecutionGate gate,
        Microsoft.Extensions.Logging.ILogger logger,
        AgentExecutionAdmissionCoordinator? admission = null,
        TimeSpan? maintenanceTimeout = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _admission = admission;
        _maintenanceTimeout = maintenanceTimeout ?? DefaultMaintenanceTimeout;
    }

    /// <summary>在途维护请求数（测试与诊断用）。</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <summary>在途维护的可诊断快照（reason + 是否已被抢占）。</summary>
    internal IReadOnlyList<string> DescribeInFlight()
        => _inFlight.Values
            .Select(v => $"{v.Reason}{(v.IsCancellationRequested ? "(cancelled)" : string.Empty)}")
            .ToArray();

    /// <summary>
    /// 调度一次终态后的维护。
    /// </summary>
    /// <param name="workspaceId">工作区（与消息受理同键）。</param>
    /// <param name="sessionId">会话（调度键 + 前台抢占键）。</param>
    /// <param name="reason">日志用原因。</param>
    /// <param name="work">维护工作；其 token 会被前台预留取消。</param>
    /// <param name="candidateKey">
    /// 候选身份（例如「同一历史 revision 的同一压缩候选」）。与在途项相同 ⇒ 合并（返回 false），
    /// 不抢占也不重复执行。
    /// </param>
    /// <returns>true = 已排队（可能抢占更早的不同候选）；false = 与在途同候选合并，或调度器正在停机。</returns>
    internal bool Schedule(
        string workspaceId,
        string sessionId,
        string reason,
        Func<CancellationToken, Task> work,
        string? candidateKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(work);

        if (Volatile.Read(ref _stopping) != 0)
            return false;

        var entry = new InFlight(reason, candidateKey, work);

        while (true)
        {
            if (_inFlight.TryGetValue(sessionId, out var previous))
            {
                // 同候选：合并（不抢占、不重复执行）。
                if (previous.CandidateKey is not null
                    && string.Equals(previous.CandidateKey, candidateKey, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "[PostTurnMaintenance] Merged duplicate candidate session={Session} reason={Reason} candidate={Candidate}",
                        sessionId, reason, candidateKey);
                    return false;
                }

                // 不同候选：最新者胜（旧候选的历史状态已被本次取代）。
                previous.Cancel();
                _logger.LogInformation(
                    "[PostTurnMaintenance] Preempting earlier maintenance session={Session} previousReason={Previous} reason={Reason}",
                    sessionId, previous.Reason, reason);

                if (!_inFlight.TryUpdate(sessionId, entry, previous))
                    continue; // 状态被并发改写，重读再决定

                _ = RunAsync(workspaceId, sessionId, entry);
                return true;
            }

            if (_inFlight.TryAdd(sessionId, entry))
            {
                _ = RunAsync(workspaceId, sessionId, entry);
                return true;
            }
        }
    }

    /// <summary>
    /// 停机：取消所有在途维护并等待它们退出（超时后保留可诊断状态，不阻塞停机）。
    /// </summary>
    internal async Task StopAsync(TimeSpan? timeout = null)
    {
        Interlocked.Exchange(ref _stopping, 1);
        var pending = _inFlight.Values.ToArray();
        foreach (var entry in pending)
            entry.Cancel();

        if (pending.Length == 0)
            return;

        var all = Task.WhenAll(pending.Select(p => p.Completion));
        var completed = await Task.WhenAny(all, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, all))
        {
            _logger.LogWarning(
                "[PostTurnMaintenance] Stop timed out; still in flight: {InFlight}",
                string.Join(", ", DescribeInFlight()));
        }
    }

    /// <summary>等待所有在途维护结束（测试用；生产停机走 <see cref="StopAsync"/>）。</summary>
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

    private async Task RunAsync(string workspaceId, string sessionId, InFlight entry)
    {
        IDisposable? backgroundLease = null;
        try
        {
            if (entry.IsCancellationRequested)
                return;

            // ① 前台准入：按会话注册后台工作。无前台需求才注册成功；
            //    注册用的就是本任务的 CTS ⇒ 新消息受理/前台租约会立刻取消本任务（复核 §3 探针）。
            if (_admission is not null)
            {
                backgroundLease = _admission.TryRegisterSessionBackground(
                    workspaceId, sessionId, entry.CancellationSource);
                if (backgroundLease is null)
                {
                    // 已有前台需求（或已有同会话后台工作）⇒ 本次可选维护直接放弃，不排队阻塞。
                    _logger.LogInformation(
                        "[PostTurnMaintenance] Skipped (foreground demand) session={Session} reason={Reason}",
                        sessionId, entry.Reason);
                    return;
                }
            }

            // ② 等本轮前台租约释放：Turn 持有租约到方法返回，因此这一步就是「终态提交之后」。
            //    立刻释放，不长期占锁（D2：维护不得阻塞新消息）。
            await using (await _gate.EnterAsync(sessionId, "post_turn_maintenance", entry.Token))
            {
            }

            entry.Token.ThrowIfCancellationRequested();

            // ③ 启动前**再次**校验前台需求：注册与真正开工之间存在窗口。
            if (_admission is not null
                && _admission.HasSessionForegroundDemand(workspaceId, sessionId))
            {
                _logger.LogInformation(
                    "[PostTurnMaintenance] Skipped (foreground demand after gate probe) session={Session} reason={Reason}",
                    sessionId, entry.Reason);
                return;
            }

            // ④ 真正干活：与其它压缩的互斥由 ContextCompactionService 的 per-session 单飞锁承担；
            //    deadline 到期即协作取消。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(entry.Token);
            timeoutCts.CancelAfter(_maintenanceTimeout);
            await entry.Work(timeoutCts.Token);
            _logger.LogInformation(
                "[PostTurnMaintenance] Completed session={Session} reason={Reason}",
                sessionId, entry.Reason);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "[PostTurnMaintenance] Cancelled (superseded or deadline) session={Session} reason={Reason}",
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
            backgroundLease?.Dispose();
            entry.Complete();
            // 原子按键值对移除：只可能移除**本项**，绝不会误删后来者（R2 §4）。
            ((ICollection<KeyValuePair<string, InFlight>>)_inFlight)
                .Remove(new KeyValuePair<string, InFlight>(sessionId, entry));
        }
    }

    private sealed class InFlight(string reason, string? candidateKey, Func<CancellationToken, Task> work)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Reason { get; } = reason;

        public string? CandidateKey { get; } = candidateKey;

        public Func<CancellationToken, Task> Work { get; } = work;

        public CancellationToken Token => _cts.Token;

        public CancellationTokenSource CancellationSource => _cts;

        public bool IsCancellationRequested => _cts.IsCancellationRequested;

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
