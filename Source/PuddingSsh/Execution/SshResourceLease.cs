using PuddingSsh.Contracts;

namespace PuddingSsh.Execution;

/// <summary>清理结果：<see cref="Completed"/> 为 false 表示已进入隔离区（预算内没结束）。</summary>
internal readonly record struct SshCleanupOutcome(
    bool Completed,
    bool Quarantined,
    bool AbortRequested,
    int PendingReaders);

/// <summary>
/// 单次操作的**资源租约**（ADR-096 D3）：谁在什么时候释放身份与独占连接，由它统一记账。
/// <list type="bullet">
///   <item>准入槽位：<see cref="ReleaseAdmission"/> 幂等释放，异常路径也必须走到。</item>
///   <item>身份材料：<see cref="ReleaseIdentity"/> 幂等；阶段超时后所有权交给迟到 owner，
///         业务路径**不得**提前清零（否则仍在读 keyStream 的迟到连接会看到被清零的密钥）。</item>
///   <item>独占连接：<see cref="RequestAbort"/> 非阻塞登记中止，<see cref="CloseSessionAsync"/>
///         在**共享清理预算**内等待关闭；预算内没结束就交给隔离区继续追踪（仍占用计数）。</item>
/// </list>
/// </summary>
internal sealed class SshResourceLease
{
    private readonly SshCleanupQuarantine _quarantine;
    private readonly Action _releaseAdmission;
    private SshIdentityMaterial? _identity;
    private ISshTransportSession? _session;
    private int _identityReleased;
    private int _admissionReleased;
    private int _lateOwnership;
    private SshCleanupOutcome? _closeOutcome;
    private readonly object _closeGate = new();

    public SshResourceLease(SshCleanupQuarantine quarantine, Action releaseAdmission)
    {
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _releaseAdmission = releaseAdmission ?? throw new ArgumentNullException(nameof(releaseAdmission));
    }

    public bool LateOwnershipTransferred => Volatile.Read(ref _lateOwnership) == 1;

    /// <summary>是否已经持有过独占连接（用于「必须走有界关闭」的兜底判断）。</summary>
    public bool HasSession => _session is not null;

    public void AttachIdentity(SshIdentityMaterial identity) => _identity = identity;

    public void AttachSession(ISshTransportSession session) => _session = session;

    /// <summary>
    /// 阶段超时：把身份与（可能迟到的）会话交给迟到 owner 收尾。调用后业务路径不再释放身份。
    /// </summary>
    public void TransferLateOwnership() => Volatile.Write(ref _lateOwnership, 1);

    /// <summary>幂等释放身份材料（清零内存）。</summary>
    public void ReleaseIdentity()
    {
        if (Interlocked.Exchange(ref _identityReleased, 1) != 0)
        {
            return;
        }

        _identity?.Dispose();
    }

    /// <summary>幂等释放准入槽位。</summary>
    public void ReleaseAdmission()
    {
        if (Interlocked.Exchange(ref _admissionReleased, 1) == 0)
        {
            _releaseAdmission();
        }
    }

    /// <summary>
    /// 非阻塞中止 + 有界关闭。预算用尽时把未完成的关闭交给隔离区（它负责观察异常并在真正结束时释放计数）。
    /// </summary>
    public async Task<SshCleanupOutcome> CloseSessionAsync(TimeSpan budget)
    {
        // 幂等：同一租约只会真正关闭一次；重复调用返回同一结论（不会重复计入隔离区）。
        lock (_closeGate)
        {
            if (_closeOutcome is { } cached)
            {
                return cached;
            }
        }

        var session = _session;
        if (session is null)
        {
            return new SshCleanupOutcome(true, false, false, 0);
        }

        // 非阻塞中止请求：真正的 TERM/关闭由会话自己的状态机执行。
        try
        {
            session.RequestAbort();
        }
        catch (Exception)
        {
            // 中止请求不得把清理路径变成失败路径。
        }

        Task disposeTask;
        try
        {
            disposeTask = session.DisposeAsync().AsTask();
        }
        catch (Exception)
        {
            return new SshCleanupOutcome(true, false, true, PendingReaders(session));
        }

        var completed = await SshBoundedDrain
            .WaitAsync(disposeTask, budget, onTimeout: static () => { })
            .ConfigureAwait(false);

        if (completed)
        {
            var done = new SshCleanupOutcome(true, false, true, PendingReaders(session));
            lock (_closeGate)
            {
                _closeOutcome = done;
            }

            return done;
        }

        // 预算内没结束：交给隔离区继续追踪（不得声称「已释放独占连接」）。
        _quarantine.Track(disposeTask);
        var pending = new SshCleanupOutcome(false, true, true, PendingReaders(session));
        lock (_closeGate)
        {
            _closeOutcome = pending;
        }

        return pending;
    }

    private static int PendingReaders(ISshTransportSession session) =>
        session is ISshTransportSessionDiagnostics diagnostics ? diagnostics.PendingReaders : -1;

    /// <summary>业务路径收尾：释放身份（除非已移交迟到 owner）与准入槽位；不等待连接关闭。</summary>
    public void ReleaseBusinessResources()
    {
        if (!LateOwnershipTransferred)
        {
            ReleaseIdentity();
        }

        ReleaseAdmission();
    }
}
