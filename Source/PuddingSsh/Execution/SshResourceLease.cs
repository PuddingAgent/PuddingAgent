using PuddingSsh.Contracts;

namespace PuddingSsh.Execution;

/// <summary>清理结果：<see cref="Completed"/> 为 false 表示已进入隔离区（预算内没结束）。</summary>
internal readonly record struct SshCleanupOutcome(
    bool Completed,
    bool Quarantined,
    bool AbortRequested,
    int PendingReaders);

/// <summary>
/// 单次操作的**资源租约**（ADR-096 D3 补充约束）：业务计数与资源计数分离，直到**全部**工作结束才释放。
/// <para>
/// 复核 F8 的缺口：原来只追踪 Dispose Task，业务返回时无条件释放准入槽，于是仍挂起的 exec
/// 完全脱离记账（leases/workers/quarantine 全为 0，Stop 错报已收敛）。现在：
/// <list type="bullet">
///   <item><see cref="RegisterWork"/> 在**调度之前**登记任务（identity/connect/exec/reader/close/迟到回收），
///         完成时观察 fault；<see cref="ResourcesCompleted"/> 只有在义务清零、登记集合为空后才完成。</item>
///   <item><see cref="BeginObligation"/>/<see cref="EndObligation"/> 表达「可能还会产生后续工作」的阶段；
///         迟到 owner 必须在自己的义务内完成资源登记，fire-and-forget 被禁止。</item>
///   <item><see cref="EndBusiness"/> 只结束业务计数：有挂起工作时**不**释放准入槽，
///         除非隔离区原子预留成功（<see cref="TryTransferToQuarantine"/>）。</item>
///   <item><see cref="Closed"/> 是**关闭证据**（会话自报 Closed），不是「异常已观察」。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SshResourceLease
{
    private readonly SshCleanupQuarantine _quarantine;

    private readonly Action _releaseAdmission;
    private readonly Func<bool>? _transferAllowed;
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly TaskCompletionSource _resourcesCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _failures = [];

    private readonly SshIdentityOwner _identityOwner = new();
    private ISshTransportSession? _session;
    private ISshTransferSession? _transferSession;
    private int _obligations;
    private int _admissionReleased;
    private int _businessEnded;
    private int _transferred;
    private int _closed;
    private Task<SshCleanupOutcome>? _closeTask;
    private readonly object _closeGate = new();

    public SshResourceLease(
        SshCleanupQuarantine quarantine,
        Action releaseAdmission,
        Func<bool>? transferAllowed = null)
    {
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _releaseAdmission = releaseAdmission ?? throw new ArgumentNullException(nameof(releaseAdmission));
        _transferAllowed = transferAllowed;
    }

    /// <summary>是否持有过独占连接（执行会话或传输会话）。</summary>
    public bool HasSession => _session is not null || _transferSession is not null;

    /// <summary>已登记、尚未结束的工作任务数。</summary>
    public int PendingWork
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>未结算的义务数（可能还会产生后续工作的阶段）。</summary>
    public int Obligations => Volatile.Read(ref _obligations);

    /// <summary>清理失败的次数（faulted 的登记任务）。</summary>
    public int CleanupFailures
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count;
            }
        }
    }

    /// <summary>清理失败原因（稳定 token，不含第三方载荷）。</summary>
    public IReadOnlyList<string> CleanupFailureReasons
    {
        get
        {
            lock (_gate)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <summary>关闭证据：会话自报已关闭。仅「调用过 Abort」或「异常已观察」不算。</summary>
    public bool Closed => Volatile.Read(ref _closed) == 1;

    public bool TransferredToQuarantine => Volatile.Read(ref _transferred) == 1;

    public bool BusinessEnded => Volatile.Read(ref _businessEnded) == 1;

    /// <summary>业务返回且资源全部结束时完成。</summary>
    public Task ResourcesCompleted => _resourcesCompleted.Task;

    /// <summary>身份材料的所有权状态（诊断/门禁用）。</summary>
    public SshIdentityOwnership IdentityState => _identityOwner.State;

    /// <summary>身份释放次数（幂等验证：恰好一次）。</summary>
    public int IdentityReleaseCount => _identityOwner.ReleaseCount;

    public void AttachIdentity(SshIdentityMaterial identity) => _identityOwner.Attach(identity);

    /// <summary>L2：在**调用 connect 之前**把所有权转入 Connecting。</summary>
    public void BeginIdentityConnect() => _identityOwner.BeginConnect();

    /// <summary>L2：connect 成功后材料改由会话资源持有。</summary>
    public void MarkIdentitySessionOwned() => _identityOwner.MarkSessionOwned();

    public void AttachSession(ISshTransportSession session) => _session = session;

    /// <summary>切片 B：挂上 SFTP 传输会话（与执行会话互斥使用）。</summary>
    public void AttachTransferSession(ISshTransferSession session) => _transferSession = session;

    /// <summary>独占的 SFTP 传输会话。</summary>
    public ISshTransferSession? TransferSession => _transferSession;

    /// <summary>在调度一个**可能产生迟到工作**的阶段之前调用；其及时路径与迟到 owner 各结算一次。</summary>
    public void BeginObligation() => Interlocked.Increment(ref _obligations);

    /// <summary>结算义务：最后一个义务结算且登记集合为空时才可能完成资源。</summary>
    public void EndObligation()
    {
        if (Interlocked.Decrement(ref _obligations) == 0)
        {
            TryCompleteResources();
        }
    }

    /// <summary>
    /// 登记一个尚未完成的任务（**必须在调度/等待之前调用**）。已完成的任务会被立即观察。
    /// </summary>
    public void RegisterWork(Task task, string kind)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCompleted)
        {
            Observe(task, kind);
            TryCompleteResources();
            return;
        }

        lock (_gate)
        {
            _pending.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var (lease, workKind) = ((SshResourceLease, string))state!;
                lease.CompleteWork(completed, workKind);
            },
            (this, kind),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>记录关闭证据（会话自报 Closed）。</summary>
    public void MarkClosed() => Volatile.Write(ref _closed, 1);

    /// <summary>把会话自报的关闭事实同步进租约。</summary>
    public void CaptureCloseEvidence(ISshTransportSession session)
    {
        if (session is ISshTransportSessionDiagnostics { Closed: true })
        {
            MarkClosed();
        }
    }

    /// <summary>
    /// 业务路径结束：**只结束业务计数**。仍有挂起工作时保留准入槽，除非隔离区原子预留成功。
    /// </summary>
    public void EndBusiness()
    {
        // 幂等：业务计数只结束一次（多个 finally 路径可能都调用）。
        if (Interlocked.Exchange(ref _businessEnded, 1) != 0)
        {
            return;
        }

        if (PendingWork == 0 && Obligations == 0)
        {
            ReleaseAdmission();
            CompleteResources();
            return;
        }

        // 只有成功预留隔离令牌才允许释放原准入槽；失败则保留原槽直到资源真正结束。
        if (TryTransferToQuarantine())
        {
            ReleaseAdmission();
        }
    }

    /// <summary>原子预留隔离令牌；成功表示资源收尾已交给隔离区（由它释放令牌）。</summary>
    public bool TryTransferToQuarantine()
    {
        if (TransferredToQuarantine)
        {
            return true;
        }

        if (_transferAllowed is { } allowed && !allowed())
        {
            return false;
        }

        if (!_quarantine.TryReserve())
        {
            return false;
        }

        Volatile.Write(ref _transferred, 1);

        // 资源整体结束时释放隔离令牌（无论任务本身是否 fault）。
        _ = ResourcesCompleted.ContinueWith(
            static (_, state) => ((SshCleanupQuarantine)state!).ReleaseReservation(),
            _quarantine,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return true;
    }

    /// <summary>幂等释放身份材料（只在整个租约的资源真正结束后由本类调用）。</summary>
    public void ReleaseIdentity() => _identityOwner.Release();

    /// <summary>幂等释放准入槽位。</summary>
    public void ReleaseAdmission()
    {
        if (Interlocked.Exchange(ref _admissionReleased, 1) == 0)
        {
            _releaseAdmission();
        }
    }

    /// <summary>业务计数收尾（由协调器调用）：只有资源全部结束才释放准入与身份。</summary>
    public void ReleaseBusinessResources() => EndBusiness();

    /// <summary>独占连接（关闭工作由协调器在有界关闭池上执行）。</summary>
    public ISshTransportSession? Session => _session;

    /// <summary>是否已经开始关闭（单次状态机已启动）。</summary>
    public bool CloseStarted
    {
        get
        {
            lock (_closeGate)
            {
                return _closeTask is not null;
            }
        }
    }

    /// <summary>
    /// **真正的单次关闭状态机**（L3）：并发调用共享同一个关闭 Task，而不是「结束后缓存结论」。
    /// 第二个调用者拿到同一个 Task，因此不会重复发 TERM/Dispose。
    /// </summary>
    public Task<SshCleanupOutcome> CloseOnceAsync(Func<Task<SshCleanupOutcome>> closeWork)
    {
        ArgumentNullException.ThrowIfNull(closeWork);
        lock (_closeGate)
        {
            return _closeTask ??= closeWork();
        }
    }

    private void CompleteWork(Task task, string kind)
    {
        Observe(task, kind);

        lock (_gate)
        {
            _pending.Remove(task);
        }

        TryCompleteResources();
    }

    private void Observe(Task task, string kind)
    {
        if (task.IsFaulted)
        {
            lock (_gate)
            {
                _failures.Add(kind + ":" + (task.Exception?.Flatten().InnerExceptions.FirstOrDefault()?.GetType().Name ?? "fault"));
            }

            // 读取 Exception 即标记为已观察；但**不**把「异常已观察」当成「关闭成功」。
            _ = task.Exception;
        }
        else if (task.IsCanceled)
        {
            _ = task.Exception;
        }
    }

    private void TryCompleteResources()
    {
        if (Volatile.Read(ref _businessEnded) == 0)
        {
            return;
        }

        if (Obligations != 0 || PendingWork != 0)
        {
            return;
        }

        CompleteResources();
    }

    private void CompleteResources()
    {
        // 资源已收敛：释放准入槽（幂等）与身份材料（幂等，且只在此刻之后——迟到连接读取期间材料保持有效）。
        ReleaseAdmission();
        ReleaseIdentity();

        if (!_resourcesCompleted.Task.IsCompleted)
        {
            _resourcesCompleted.TrySetResult();
        }
    }
}
