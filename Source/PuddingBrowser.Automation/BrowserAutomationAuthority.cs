using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Automation;

/// <summary>
/// 统一控制权与页面授权的<b>唯一事实源</b>（设计方案 §3.1）。
///
/// 为什么必须是唯一实例：审计已证实进程内存在三份互不相通的 takeover/paused 表示，而产品 Shell
/// 只写其中最不权威的一份 ⇒ 用户点「人工接管」对 Agent 没有约束力。这里把状态收敛成一份，
/// 消费方（Bridge dispatcher / DesktopService / 工作区控制器 / Shell）只读它，不再各自维护标志。
///
/// 语义要点：
/// • 接管/暂停/恢复/关闭/连接世代改变都推进控制世代；<b>接管不撤销阅读授权</b>；
/// • 撤销授权、切目标、关闭、世代改变推进授权世代；
/// • 写请求入队前 <see cref="Admit"/> 签发租约，触碰页面前 <see cref="Revalidate"/> 复检世代 ⇒
///   「验证通过 → 期间被接管」必然被拦下（ABA 竞态由单调世代消除）；
/// • 同一页面目标同时只有一个写租约（父/子代理不得并发持有同页写租约）。
///
/// 线程安全：全部状态在 <see cref="Lock"/> 下读写；UI 线程与后台线程都可调用。
/// </summary>
public sealed class BrowserAutomationAuthority : IBrowserAutomationAuthority, IBrowserAutomationControl
{
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly DesktopInstanceId _desktopId;
    private readonly DesktopProcessInstanceId _processInstanceId;
    private readonly Dictionary<string, BrowserPageGrant> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserManagementGrant> _managementGrants = new(StringComparer.Ordinal);

    private BrowserControlEpoch _controlEpoch = BrowserControlEpoch.Initial;
    private BrowserPageGrantEpoch _grantEpoch = BrowserPageGrantEpoch.Initial;
    private ConnectionGeneration _generation = ConnectionGeneration.None;
    private bool _isPaused;
    private bool _isUserTakeover;
    private bool _isClosed;
    private string? _reason;
    private BrowserAutomationLease? _lease;

    public BrowserAutomationAuthority(
        DesktopInstanceId desktopId,
        DesktopProcessInstanceId processInstanceId,
        TimeProvider? timeProvider = null)
    {
        _desktopId = desktopId ?? throw new ArgumentNullException(nameof(desktopId));
        _processInstanceId = processInstanceId ?? throw new ArgumentNullException(nameof(processInstanceId));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ── 读取面 ────────────────────────────────────────────────────────────

    public BrowserControlSnapshot Capture()
    {
        lock (_sync)
        {
            return Snapshot();
        }
    }

    public IReadOnlyList<BrowserPageGrant> ActiveGrants
    {
        get
        {
            lock (_sync)
            {
                return _grants.Values.ToArray();
            }
        }
    }

    public IReadOnlyList<BrowserManagementGrant> ActiveManagementGrants
    {
        get
        {
            lock (_sync)
            {
                return _managementGrants.Values.ToArray();
            }
        }
    }

    // ── 第一次验证：入队前 ────────────────────────────────────────────────

    public BrowserAuthorizationDecision Admit(BrowserAutomationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var control = Snapshot();

            var gate = GateConnectionAndIdentity(request, control);
            if (gate is not null)
            {
                return gate;
            }

            // 接管/暂停期间：副作用操作一律拒绝；只读观察继续（方案 §3.2 表格）。
            var blocked = BlockedWhileControlling(out var blockedError);
            if (blocked is not null && BrowserOperationPolicy.IsBlockedWhileUserControlling(request.Operation))
            {
                return Deny(blocked.Value, blockedError!, control);
            }

            switch (BrowserOperationPolicy.RequiredGrantKind(request.Operation))
            {
                case BrowserGrantKind.None:
                    // 浏览器作用域元数据：只需可信身份（接管期间同样允许有界元数据读取）。
                    return BrowserAuthorizationDecision.Allow(control);

                case BrowserGrantKind.Management:
                    return HasManagementGrant(request)
                        ? BrowserAuthorizationDecision.Allow(control)
                        : Deny(
                            BrowserAuthorizationDenial.NoManagementGrant,
                            DesktopCapabilityError.Unauthorized(
                                "no task-level management grant allows creating or closing browser targets for this caller"),
                            control);

                default:
                    return AdmitPageOperation(request, control);
            }
        }
    }

    private BrowserAuthorizationDecision AdmitPageOperation(
        BrowserAutomationRequest request,
        BrowserControlSnapshot control)
    {
        var (grant, denial) = FindPageGrant(request);
        if (grant is null)
        {
            return Deny(denial, DescribeDenial(denial, request), control);
        }

        if (!request.RequiresControlLease)
        {
            return BrowserAuthorizationDecision.Allow(control);
        }

        // 写操作：按页串行化。同一 operationId 的重传复用既有租约（不产生第二个租约）。
        if (_lease is not null)
        {
            return _lease.Allows(request)
                ? BrowserAuthorizationDecision.Allow(control, _lease)
                : Deny(
                    BrowserAuthorizationDenial.PageBusy,
                    DesktopCapabilityError.ResourceExhausted(
                        $"page '{request.Target!.Key}' already has an in-flight write lease"),
                    control);
        }

        var lease = new BrowserAutomationLease(
            BrowserAutomationLeaseId.NewId(),
            request.Target!,
            request.Caller,
            request.OperationId,
            _controlEpoch,
            _grantEpoch,
            _timeProvider.GetUtcNow());

        _lease = lease;
        return BrowserAuthorizationDecision.Allow(Snapshot(), lease);
    }

    // ── 第二次验证：实际触碰页面前 ────────────────────────────────────────

    public BrowserAuthorizationDecision Revalidate(BrowserAutomationLease lease, BrowserAutomationRequest request)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var control = Snapshot();

            // 控制状态优先报告：接管/暂停比「世代变了」更贴近用户看到的事实。
            var blocked = BlockedWhileControlling(out var blockedError);
            if (blocked is not null)
            {
                return Deny(blocked.Value, blockedError!, control);
            }

            if (_lease is null
                || !string.Equals(_lease.LeaseId.Value, lease.LeaseId.Value, StringComparison.Ordinal))
            {
                return Deny(
                    BrowserAuthorizationDenial.LeaseLost,
                    DesktopCapabilityError.ResourceExhausted(
                        "the write lease is no longer held; re-admit the operation before touching the page"),
                    control);
            }

            if (!lease.Allows(request))
            {
                return Deny(
                    BrowserAuthorizationDenial.CallerMismatch,
                    DesktopCapabilityError.Unauthorized(
                        "the write lease belongs to a different operation, caller or page target"),
                    control);
            }

            if (lease.IsExpiredAt(_timeProvider.GetUtcNow()))
            {
                return Deny(
                    BrowserAuthorizationDenial.LeaseLost,
                    DesktopCapabilityError.ResourceExhausted("the write lease has expired"),
                    control);
            }

            if (lease.ControlEpoch.Value != _controlEpoch.Value || lease.GrantEpoch.Value != _grantEpoch.Value)
            {
                return Deny(
                    BrowserAuthorizationDenial.EpochStale,
                    DesktopCapabilityError.InvalidTarget(
                        "the captured control/authorization epoch changed while the operation was queued; re-admit it"),
                    control);
            }

            var (grant, denial) = FindPageGrant(request);
            return grant is null
                ? Deny(denial, DescribeDenial(denial, request), control)
                : BrowserAuthorizationDecision.Allow(control, lease);
        }
    }

    // ── 控制面：接管 / 暂停 / 恢复 / 关闭 / 世代 ──────────────────────────

    public BrowserControlSnapshot SetPaused(bool paused, string? reason = null)
    {
        lock (_sync)
        {
            if (_isClosed || _isPaused == paused)
            {
                return Snapshot();
            }

            _isPaused = paused;
            _reason = reason;
            AdvanceControlEpoch();
            ReleaseLeaseInternal();
            return Snapshot();
        }
    }

    public BrowserControlSnapshot SetUserTakeover(bool takenOver, string? reason = null)
    {
        lock (_sync)
        {
            if (_isClosed || _isUserTakeover == takenOver)
            {
                return Snapshot();
            }

            _isUserTakeover = takenOver;
            _reason = reason;
            AdvanceControlEpoch();
            // 接管不自动撤销阅读授权：授权世代保持不变（方案 §3.1）。
            ReleaseLeaseInternal();
            return Snapshot();
        }
    }

    public BrowserControlSnapshot Resume(string? reason = null)
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                return Snapshot();
            }

            var hadHold = _isPaused || _isUserTakeover;
            _isPaused = false;
            _isUserTakeover = false;
            _reason = reason;

            if (hadHold)
            {
                // 推进世代 ⇒ 接管前的队列与租约不会复活（方案 G1）。
                AdvanceControlEpoch();
            }

            ReleaseLeaseInternal();
            return Snapshot();
        }
    }

    public BrowserControlSnapshot Close(string? reason = null)
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                return Snapshot();
            }

            _isClosed = true;
            _reason = reason;
            AdvanceControlEpoch();
            AdvanceGrantEpoch();
            _grants.Clear();
            _managementGrants.Clear();
            ReleaseLeaseInternal();
            return Snapshot();
        }
    }

    public BrowserControlSnapshot NotifyConnectionGeneration(
        ConnectionGeneration generation,
        string? reason = null)
    {
        lock (_sync)
        {
            if (generation.Value == _generation.Value)
            {
                return Snapshot();
            }

            _generation = generation;
            _reason = reason;

            // 世代改变 ⇒ 旧世代的命令、许可与租约全部作废；恢复连接需重新获取许可与状态。
            AdvanceControlEpoch();
            AdvanceGrantEpoch();
            _grants.Clear();
            _managementGrants.Clear();
            ReleaseLeaseInternal();
            return Snapshot();
        }
    }

    public BrowserControlSnapshot ReleaseLease(BrowserAutomationLeaseId leaseId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(leaseId);

        lock (_sync)
        {
            if (_lease is { } lease
                && string.Equals(lease.LeaseId.Value, leaseId.Value, StringComparison.Ordinal))
            {
                _lease = null;

                if (reason is not null)
                {
                    _reason = reason;
                }
            }

            return Snapshot();
        }
    }

    // ── 控制面：授权签发与撤销 ────────────────────────────────────────────

    public BrowserPageGrantEpoch IssueGrant(BrowserPageGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        lock (_sync)
        {
            EnsureLiveBinding(grant.Binding);
            BrowserGrantIssuancePolicy.EnsureSubAgentScopeIsSubset(grant, _grants.Values);
            _grants[grant.GrantId.Value] = grant;
            return _grantEpoch;
        }
    }

    public BrowserPageGrantEpoch IssueManagementGrant(BrowserManagementGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        lock (_sync)
        {
            EnsureLiveBinding(grant.Binding);
            _managementGrants[grant.GrantId.Value] = grant;
            return _grantEpoch;
        }
    }

    public BrowserPageGrantEpoch RevokeGrant(BrowserPageGrantId grantId)
    {
        ArgumentNullException.ThrowIfNull(grantId);

        lock (_sync)
        {
            if (_grants.Remove(grantId.Value))
            {
                AdvanceGrantEpoch();
                ReleaseLeaseIfUnauthorized();
            }

            return _grantEpoch;
        }
    }

    public BrowserPageGrantEpoch RevokeManagementGrant(BrowserManagementGrantId grantId)
    {
        ArgumentNullException.ThrowIfNull(grantId);

        lock (_sync)
        {
            if (_managementGrants.Remove(grantId.Value))
            {
                AdvanceGrantEpoch();
            }

            return _grantEpoch;
        }
    }

    public BrowserPageGrantEpoch RevokeExecutionScope(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            var removed = 0;
            foreach (var grant in _grants.Values.Where(grant => grant.TargetsSamePage(target)).ToArray())
            {
                var remaining = grant.Scope & ~BrowserPageGrantScope.Write & ~BrowserPageGrantScope.Manage;
                if (remaining == BrowserPageGrantScope.None)
                {
                    _grants.Remove(grant.GrantId.Value);
                }
                else
                {
                    // 记录不可变：用缩减后的范围重建同一条授权（ID/绑定/签发时间保持不变）。
                    _grants[grant.GrantId.Value] = new BrowserPageGrant(
                        grant.GrantId,
                        grant.Binding,
                        grant.Target,
                        grant.Grantee,
                        remaining,
                        grant.Epoch,
                        grant.IssuedAtUtc,
                        grant.FrameId,
                        grant.ExpiresAtUtc);
                }

                removed++;
            }

            if (removed > 0)
            {
                AdvanceGrantEpoch();
                ReleaseLeaseIfUnauthorized();
            }

            return _grantEpoch;
        }
    }

    public BrowserPageGrantEpoch RevokeAllGrants()
    {
        lock (_sync)
        {
            if (_grants.Count == 0 && _managementGrants.Count == 0)
            {
                return _grantEpoch;
            }

            _grants.Clear();
            _managementGrants.Clear();
            AdvanceGrantEpoch();
            ReleaseLeaseInternal();
            return _grantEpoch;
        }
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    private BrowserPageGrantBinding CurrentBinding =>
        new(_desktopId, _processInstanceId, _generation);

    private BrowserControlSnapshot Snapshot() =>
        new(_controlEpoch, _grantEpoch, _isPaused, _isUserTakeover, _isClosed, _lease, _reason);

    private void AdvanceControlEpoch() => _controlEpoch = _controlEpoch.Next();

    private void AdvanceGrantEpoch() => _grantEpoch = _grantEpoch.Next();

    private void ReleaseLeaseInternal() => _lease = null;

    private void EnsureLiveBinding(BrowserPageGrantBinding binding)
    {
        if (!_generation.IsLive)
        {
            throw new InvalidOperationException(
                "no live connection generation; the authority cannot bind a grant before the handshake.");
        }

        if (binding.ConnectionGeneration.Value != _generation.Value)
        {
            throw new ArgumentException(
                $"the grant is bound to connection generation {binding.ConnectionGeneration.Value}"
                + $" but the live generation is {_generation.Value}.",
                nameof(binding));
        }
    }

    /// <summary>授权被撤销后，租约不得比它的授权活得久。</summary>
    private void ReleaseLeaseIfUnauthorized()
    {
        if (_lease is not { } lease)
        {
            return;
        }

        if (!HasWriteCoverage(lease.Target, lease.Holder))
        {
            _lease = null;
        }
    }

    /// <summary>是否仍存在覆盖该目标的 Write 授权（用于「租约不越过授权」的不变式）。</summary>
    private bool HasWriteCoverage(DesktopPageTarget target, BrowserCallerIdentity holder)
    {
        var now = _timeProvider.GetUtcNow();
        var binding = CurrentBinding;

        foreach (var grant in _grants.Values)
        {
            if (!grant.TargetsSamePage(target)
                || !string.Equals(grant.Grantee.CallerId, holder.CallerId, StringComparison.Ordinal)
                || !grant.Binding.Matches(binding)
                || grant.IsExpiredAt(now)
                || !grant.Covers(BrowserPageGrantScope.Write))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private BrowserAuthorizationDecision? GateConnectionAndIdentity(
        BrowserAutomationRequest request,
        BrowserControlSnapshot control)
    {
        if (!_generation.IsLive)
        {
            return Deny(
                BrowserAuthorizationDenial.NotConnected,
                DesktopCapabilityError.NotConnected(
                    "the desktop connection is not established; no automation permit exists"),
                control);
        }

        if (_isClosed)
        {
            return Deny(
                BrowserAuthorizationDenial.Closed,
                DesktopCapabilityError.UiUnavailable("browser automation is closed"),
                control);
        }

        if (request.ConnectionGeneration.Value != _generation.Value)
        {
            return Deny(
                BrowserAuthorizationDenial.EpochStale,
                DesktopCapabilityError.InvalidTarget(
                    $"the request targets connection generation {request.ConnectionGeneration.Value}"
                    + $" but the live generation is {_generation.Value}"),
                control);
        }

        if (!request.Caller.IsTrustedAutomationCaller)
        {
            return Deny(
                BrowserAuthorizationDenial.UntrustedCaller,
                DesktopCapabilityError.Unauthorized(
                    "browser automation requires a trusted agent caller identity"),
                control);
        }

        return null;
    }

    private BrowserAuthorizationDenial? BlockedWhileControlling(out DesktopCapabilityError? error)
    {
        error = null;

        if (_isClosed)
        {
            error = DesktopCapabilityError.UiUnavailable("browser automation is closed");
            return BrowserAuthorizationDenial.Closed;
        }

        if (_isUserTakeover)
        {
            error = new DesktopCapabilityError(
                DesktopCapabilityErrorCode.UserTakeover,
                AppendReason("user took over the browser; automation is stopped"));
            return BrowserAuthorizationDenial.UserTakeover;
        }

        if (_isPaused)
        {
            error = new DesktopCapabilityError(
                DesktopCapabilityErrorCode.Paused,
                AppendReason("the tool runtime is paused"));
            return BrowserAuthorizationDenial.Paused;
        }

        return null;
    }

    private string AppendReason(string message) =>
        string.IsNullOrWhiteSpace(_reason) ? message : $"{message} ({_reason})";

    private bool HasManagementGrant(BrowserAutomationRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        var binding = CurrentBinding;

        foreach (var grant in _managementGrants.Values)
        {
            if (!grant.Grantee.IsSameTaskScope(request.Caller))
            {
                continue;
            }

            if (!grant.Binding.Matches(binding) || grant.IsExpiredAt(now))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private (BrowserPageGrant? Grant, BrowserAuthorizationDenial Denial) FindPageGrant(
        BrowserAutomationRequest request)
    {
        if (request.Target is not { } target)
        {
            return (null, BrowserAuthorizationDenial.MissingTarget);
        }

        var now = _timeProvider.GetUtcNow();
        var binding = CurrentBinding;
        var sawCandidatePage = false;
        var lastDenial = BrowserAuthorizationDenial.NoGrant;

        foreach (var grant in _grants.Values)
        {
            if (!grant.TargetsSamePage(target))
            {
                continue;
            }

            sawCandidatePage = true;

            // 页面授权必须发给**这个**调用者：子代理要自己的子集授权，不能直接借用父任务的授权
            // （方案 §3.2「子代理只能取得父任务授权的子集」）。
            if (!string.Equals(grant.Grantee.CallerId, request.Caller.CallerId, StringComparison.Ordinal))
            {
                lastDenial = BrowserAuthorizationDenial.CallerMismatch;
                continue;
            }

            if (!grant.Binding.Matches(binding))
            {
                lastDenial = BrowserAuthorizationDenial.BindingMismatch;
                continue;
            }

            if (!string.Equals(grant.FrameId, request.FrameId, StringComparison.Ordinal))
            {
                lastDenial = BrowserAuthorizationDenial.FrameMismatch;
                continue;
            }

            if (grant.IsExpiredAt(now))
            {
                lastDenial = BrowserAuthorizationDenial.GrantExpired;
                continue;
            }

            if (!grant.Covers(request.RequiredScope))
            {
                lastDenial = BrowserAuthorizationDenial.ScopeInsufficient;
                continue;
            }

            return (grant, BrowserAuthorizationDenial.None);
        }

        return (null, sawCandidatePage ? lastDenial : BrowserAuthorizationDenial.NoGrant);
    }

    private static DesktopCapabilityError DescribeDenial(
        BrowserAuthorizationDenial denial,
        BrowserAutomationRequest request)
    {
        var page = request.Target?.Key ?? "(no page target)";
        var scope = request.RequiredScope;

        return denial switch
        {
            BrowserAuthorizationDenial.NoGrant =>
                DesktopCapabilityError.Unauthorized($"no page grant covers '{page}' for this caller"),
            BrowserAuthorizationDenial.ScopeInsufficient =>
                DesktopCapabilityError.Unauthorized(
                    $"the page grant for '{page}' does not cover the required scope {scope}"),
            BrowserAuthorizationDenial.TargetMismatch =>
                DesktopCapabilityError.Unauthorized($"the page grant does not match '{page}'"),
            BrowserAuthorizationDenial.FrameMismatch =>
                DesktopCapabilityError.Unauthorized(
                    $"the page grant is bound to a different frame than '{request.FrameId ?? "(top)"}'"),
            BrowserAuthorizationDenial.GrantExpired =>
                DesktopCapabilityError.Unauthorized($"the page grant for '{page}' has expired"),
            BrowserAuthorizationDenial.BindingMismatch =>
                DesktopCapabilityError.InvalidTarget(
                    "the page grant is bound to a different desktop instance, process or connection generation"),
            BrowserAuthorizationDenial.CallerMismatch =>
                DesktopCapabilityError.Unauthorized($"the page grant for '{page}' was issued to a different caller"),
            BrowserAuthorizationDenial.MissingTarget =>
                DesktopCapabilityError.InvalidTarget(
                    $"{request.Operation} requires an explicit page target"),
            _ => DesktopCapabilityError.Unauthorized(
                $"browser automation denied for '{page}' ({BrowserAuthorizationDenialWire.NameOf(denial)})"),
        };
    }

    private static BrowserAuthorizationDecision Deny(
        BrowserAuthorizationDenial denial,
        DesktopCapabilityError error,
        BrowserControlSnapshot control) =>
        BrowserAuthorizationDecision.Deny(denial, error, control);
}
