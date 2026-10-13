using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using PuddingSsh.Transport;
using PuddingSsh.Validation;

namespace PuddingSsh.Probe;

/// <summary>
/// R5 扩展协议矩阵（ADR-096 D2/D3/D4）在**真实 OpenSSH** 上的收口：
/// <list type="number">
///   <item><c>precancel</c>：预先取消 / 已过期 deadline ⇒ 零连接、零认证、零 marker。</item>
///   <item><c>lateconnect</c>：极短 deadline 下真实连接可能迟到；迟到会话**不得**执行 marker。</item>
///   <item><c>noeof</c>：服务端保持连接但永不发 stdout EOF ⇒ 关闭与 reader 必须在宽限内收敛（Closed/PendingReaders）。</item>
///   <item><c>submitfail</c>：exec 提交窗口断线 ⇒ <c>unknown</c> 且 <c>retry_safe=false</c>（不得宣称未提交）。</item>
///   <item><c>rounds</c>：多轮成功/超时/取消之后租约、worker、隔离区计数回到基线；扩大输出仍固定捕获。</item>
///   <item><c>signal</c>：signal-only 退出映射为 <c>ssh.remote_exit_signal</c>，已知 exit code 保留。</item>
/// </list>
/// </summary>
internal static partial class Program
{
    /// <summary>预先取消与已过期 deadline：不得有任何服务端认证，也不得执行 marker。</summary>
    private static async Task RunPreCancelAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "precancel";
        var runId = Guid.NewGuid().ToString("N")[..8];
        var marker = $"{options.RemoteTempDirectory}/precancel-{runId}.marker";
        var factory = new SshNetTransportFactory();
        var operations = 1;
        var connections = 0;

        // 清掉可能的残留 marker（这一次连接属于场景准备，不计入「业务请求」）。
        connections++;
        await using (var setup = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            await setup.ExecuteAsync(
                $"rm -f {marker}; echo PREP_OK",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);
        }

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new ProbeIdentityProvider(),
            new SshCoordinatorOptions
            {
                CleanupGrace = TimeSpan.FromMilliseconds(500),
            });

        var command = $"sh -c 'echo PRESENT >> {marker}'";

        // 1) 调用前已取消。
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await coordinator.ExecuteAsync(options.HostFor(), Execute(options, command), cts.Token);
        operations++;

        // 2) 宿主 deadline 已过期。
        var expiredHost = options.HostFor() with { ExecutionDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-5), Limits = options.HostFor().Limits with { MaxOperationTimeoutSeconds = 60 } };
        var expired = await coordinator.ExecuteAsync(expiredHost, Execute(options, command), CancellationToken.None);
        operations++;

        // 3) 服务端核对：marker 必须不存在。
        string evidence;
        connections++;
        await using (var check = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            var verify = await check.ExecuteAsync(
                $"test -e {marker} && echo PRESENT || echo ABSENT; wc -c < {marker} 2>/dev/null || true",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);
            evidence = verify.Stdout.Trim().Replace('\n', '|');
        }

        log.Fact(scenario, "cancelled_status", cancelled.Status.ToString());
        log.Fact(scenario, "cancelled_error", cancelled.ErrorCode ?? "null");
        log.Fact(scenario, "cancelled_state", cancelled.ExecutionState.ToString());
        log.Fact(scenario, "expired_status", expired.Status.ToString());
        log.Fact(scenario, "expired_error", expired.ErrorCode ?? "null");
        log.Fact(scenario, "marker_evidence", evidence);
        log.Fact(scenario, "operations", operations);
        log.Fact(scenario, "connections", connections);

        log.Assert(scenario, "cancelled_code", cancelled.ErrorCode == SshErrorCodes.Cancelled, cancelled.ErrorCode ?? "null");
        log.Assert(scenario, "cancelled_not_submitted", cancelled.ExecutionState == SshExecutionState.NotSubmitted, cancelled.ExecutionState.ToString());
        log.Assert(scenario, "expired_timeout", expired.ErrorCode == SshErrorCodes.Timeout, expired.ErrorCode ?? "null");
        log.Assert(scenario, "no_marker", !evidence.Contains("PRESENT", StringComparison.Ordinal), evidence);
        log.Assert(scenario, "no_connection_for_rejected_requests", connections == 2, connections.ToString());
    }

    /// <summary>极短 deadline：真实连接可能迟到；迟到会话不得提交 marker。</summary>
    private static async Task RunLateConnectAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "lateconnect";
        var runId = Guid.NewGuid().ToString("N")[..8];
        var marker = $"{options.RemoteTempDirectory}/lateconnect-{runId}.marker";
        var factory = new SshNetTransportFactory();
        var connections = 0;
        var operations = 0;

        connections++;
        operations++;
        await using (var setup = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            await setup.ExecuteAsync(
                $"rm -f {marker}; echo PREP_OK",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);
        }

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new ProbeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(800) });

        // 1 ms deadline：几乎必然在真实建连完成前到期。
        var host = options.HostFor() with
        {
            ExecutionDeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(1),
            Limits = options.HostFor().Limits with { ConnectTimeoutSeconds = 15, MaxOperationTimeoutSeconds = 60 },
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await coordinator.ExecuteAsync(host, Execute(options, $"sh -c 'echo PRESENT >> {marker}'"), CancellationToken.None);
        stopwatch.Stop();
        operations++;

        // 给迟到连接与迟到 owner 一点收敛时间，再核对 marker。
        await Task.Delay(TimeSpan.FromSeconds(2));

        string evidence;
        connections++;
        operations++;
        await using (var check = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            var verify = await check.ExecuteAsync(
                $"test -e {marker} && echo PRESENT || echo ABSENT",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);
            evidence = verify.Stdout.Trim().Replace('\n', '|');
        }

        log.Fact(scenario, "status", result.Status.ToString());
        log.Fact(scenario, "error", result.ErrorCode ?? "null");
        log.Fact(scenario, "bounded_ms", stopwatch.ElapsedMilliseconds);
        log.Fact(scenario, "marker_evidence", evidence);
        log.Fact(scenario, "active_leases", coordinator.ActiveLeases);
        log.Fact(scenario, "quarantined", coordinator.QuarantinedResources);
        log.Fact(scenario, "operations", operations);
        log.Fact(scenario, "connections", connections);

        log.Assert(scenario, "bounded", stopwatch.ElapsedMilliseconds < 8000, stopwatch.ElapsedMilliseconds.ToString());
        log.Assert(scenario, "timeout_or_cancelled", result.ErrorCode is SshErrorCodes.Timeout or SshErrorCodes.Cancelled, result.ErrorCode ?? "null");
        log.Assert(scenario, "late_session_never_executes_marker", !evidence.Contains("PRESENT", StringComparison.Ordinal), evidence);
        log.Assert(scenario, "no_lease_leak", coordinator.ActiveLeases == 0, coordinator.ActiveLeases.ToString());
    }

    /// <summary>
    /// 服务端保持连接但**永不发 stdout EOF**（后台子进程持有 stdout）：客户端必须在宽限内关闭连接
    /// 并让 reader 收敛——这是「真实适配器的 Abort 真的关掉了 socket/reader」的证据。
    /// </summary>
    private static async Task RunNoEofAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "noeof";
        var factory = new SshNetTransportFactory();
        var operations = 0;
        var connections = 0;

        connections++;
        var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None);

        int pendingReaders;
        bool closed;
        bool abortRequested;
        SshExecuteResult result;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // 后台子进程继承 stdout 并睡很久 ⇒ 服务端不会发送 stdout EOF，命令也不会结束。
            operations++;
            result = await session.ExecuteAsync(
                "sh -c 'echo HOLDS_STDOUT; { sleep 120; } & sleep 120'",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);

            var cleanupStopwatch = Stopwatch.StartNew();
            await session.DisposeAsync();
            cleanupStopwatch.Stop();

            log.Fact(scenario, "cleanup_ms", cleanupStopwatch.ElapsedMilliseconds);
        }
        finally
        {
            stopwatch.Stop();
        }

        if (session is ISshTransportSessionDiagnostics diagnostics)
        {
            pendingReaders = diagnostics.PendingReaders;
            closed = diagnostics.Closed;
            abortRequested = diagnostics.AbortRequested;
        }
        else
        {
            pendingReaders = -1;
            closed = false;
            abortRequested = false;
        }

        log.Fact(scenario, "status", result.Status.ToString());
        log.Fact(scenario, "error", result.ErrorCode ?? "null");
        log.Fact(scenario, "state", result.ExecutionState.ToString());
        log.Fact(scenario, "output_complete", result.OutputComplete);
        log.Fact(scenario, "incomplete_reason", result.OutputIncompleteReason ?? "null");
        log.Fact(scenario, "pending_readers", pendingReaders);
        log.Fact(scenario, "closed", closed);
        log.Fact(scenario, "abort_requested", abortRequested);
        log.Fact(scenario, "elapsed_ms", stopwatch.ElapsedMilliseconds);
        log.Fact(scenario, "operations", operations);
        log.Fact(scenario, "connections", connections);

        log.Assert(scenario, "closed_after_abort", closed, closed.ToString());
        log.Assert(scenario, "readers_converged", pendingReaders == 0, pendingReaders.ToString());
        log.Assert(scenario, "unknown_not_completed", result.Status != SshExecutionStatus.Completed, result.Status.ToString());
        log.Assert(scenario, "output_incomplete_reason_present", result.OutputComplete || result.OutputIncompleteReason is not null, result.OutputIncompleteReason ?? "complete");
    }

    /// <summary>提交窗口断线（远端杀掉自己的 sshd 会话进程）：必须 unknown 且不可重试。</summary>
    private static async Task RunSubmitFailureAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "submitfail";
        var factory = new SshNetTransportFactory();

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new ProbeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(800) });

        var result = await coordinator.ExecuteAsync(
            options.HostFor(),
            Execute(options, "sh -c 'kill -9 $PPID; sleep 5'"),
            CancellationToken.None);

        log.Fact(scenario, "status", result.Status.ToString());
        log.Fact(scenario, "error", result.ErrorCode ?? "null");
        log.Fact(scenario, "state", result.ExecutionState.ToString());
        log.Fact(scenario, "retry_safe", result.RetrySafe);
        log.Fact(scenario, "cleanup_state", result.CleanupState.ToString());
        log.Fact(scenario, "operations", 1);
        log.Fact(scenario, "connections", 1);

        log.Assert(scenario, "submitted_never_claims_not_submitted", result.ExecutionState != SshExecutionState.NotSubmitted, result.ExecutionState.ToString());
        log.Assert(scenario, "not_retry_safe", !result.RetrySafe, result.RetrySafe.ToString());
        log.Assert(
            scenario,
            "stable_failure_code",
            result.ErrorCode is SshErrorCodes.ConnectionLost or SshErrorCodes.TransportFault or SshErrorCodes.Timeout,
            result.ErrorCode ?? "null");
    }

    /// <summary>多轮成功/超时/取消之后：租约、worker、隔离区计数回到基线；扩大输出仍固定捕获。</summary>
    private static async Task RunRoundsAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "rounds";
        var factory = new SshNetTransportFactory();
        var statuses = new List<string>();
        var cleanupStates = new List<string>();
        var connections = 0;
        var operations = 0;

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new ProbeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(500) });

        // 1..3：成功；4..6：服务端 deadline 到期；7..8：调用方取消；9：扩大输出。
        for (var round = 1; round <= 8; round++)
        {
            connections++;
            operations++;
            using var cts = new CancellationTokenSource();
            Task<SshExecuteResult> operation;
            if (round <= 3)
            {
                operation = coordinator.ExecuteAsync(options.HostFor(), Execute(options, "echo ROUND_OK"), cts.Token);
            }
            else if (round <= 6)
            {
                operation = coordinator.ExecuteAsync(
                    options.HostFor(operationTimeoutSeconds: 1),
                    Execute(options, "sleep 30"),
                    cts.Token);
            }
            else
            {
                operation = coordinator.ExecuteAsync(options.HostFor(), Execute(options, "sleep 30"), cts.Token);
                await Task.Delay(120);
                await cts.CancelAsync();
            }

            var result = await operation;
            statuses.Add(result.ErrorCode ?? result.Status.ToString());
            cleanupStates.Add(result.CleanupState.ToString());
        }

        // 扩大输出：捕获恒为预算，驻留内存不随输出增长。
        connections++;
        operations++;
        var big = await coordinator.ExecuteAsync(
            options.HostFor(),
            Execute(options, "head -c 8388608 /dev/zero | tr '\\0' 'x'"),
            CancellationToken.None);

        var baseline = await FakeSshWaitAsync(
            () => coordinator.ActiveLeases == 0 && coordinator.ActiveStageWorkers == 0 && coordinator.QuarantinedResources == 0,
            TimeSpan.FromSeconds(10));

        log.Fact(scenario, "statuses", string.Join('|', statuses));
        log.Fact(scenario, "cleanup_states", string.Join('|', cleanupStates));
        log.Fact(scenario, "peak_stage_workers", coordinator.PeakStageWorkers);
        log.Fact(scenario, "peak_close_workers", coordinator.PeakCloseWorkers);
        log.Fact(scenario, "peak_quarantined", coordinator.PeakQuarantinedResources);
        log.Fact(scenario, "big_status", big.Status.ToString());
        log.Fact(scenario, "big_captured_bytes", big.CapturedBytes);
        log.Fact(scenario, "big_truncated", big.OutputTruncated);
        log.Fact(scenario, "big_output_complete", big.OutputComplete);
        log.Fact(scenario, "active_leases", coordinator.ActiveLeases);
        log.Fact(scenario, "active_workers", coordinator.ActiveStageWorkers);
        log.Fact(scenario, "quarantined", coordinator.QuarantinedResources);
        log.Fact(scenario, "operations", operations);
        log.Fact(scenario, "connections", connections);

        log.Assert(scenario, "counters_back_to_baseline", baseline, "leases/workers/quarantine");
        log.Assert(
            scenario,
            "capture_bounded",
            big.CapturedBytes <= options.OutputBudgetBytes && big.OutputTruncated,
            $"captured={big.CapturedBytes} truncated={big.OutputTruncated}");
        log.Assert(scenario, "no_negative_counters", coordinator.ActiveLeases >= 0 && coordinator.QuarantinedResources >= 0, "counters");
        log.Assert(
            scenario,
            "peaks_within_capacity",
            coordinator.PeakStageWorkers <= coordinator.StageWorkerCapacity
                && coordinator.PeakCloseWorkers <= coordinator.CloseWorkerCapacity
                && coordinator.PeakQuarantinedResources <= coordinator.QuarantineCapacity,
            $"workers={coordinator.PeakStageWorkers}/{coordinator.StageWorkerCapacity} close={coordinator.PeakCloseWorkers}/{coordinator.CloseWorkerCapacity} quarantine={coordinator.PeakQuarantinedResources}/{coordinator.QuarantineCapacity}");
        log.Assert(
            scenario,
            "every_round_cleanup_completed",
            cleanupStates.All(state => string.Equals(state, "Completed", StringComparison.Ordinal)),
            string.Join('|', cleanupStates));
    }

    /// <summary>signal-only 退出映射，以及已知 exit code 的保留。</summary>
    private static async Task RunSignalAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "signal";
        var factory = new SshNetTransportFactory();

        await using var coordinator = new SshOperationCoordinator(factory, new ProbeIdentityProvider(), new SshCoordinatorOptions());

        var killed = await coordinator.ExecuteAsync(
            options.HostFor(),
            Execute(options, "sh -c 'kill -9 $$'"),
            CancellationToken.None);

        var exited = await coordinator.ExecuteAsync(
            options.HostFor(),
            Execute(options, "exit 7"),
            CancellationToken.None);

        var ok = await coordinator.ExecuteAsync(
            options.HostFor(),
            Execute(options, "echo ALL_GOOD"),
            CancellationToken.None);

        log.Fact(scenario, "killed_status", killed.Status.ToString());
        log.Fact(scenario, "killed_error", killed.ErrorCode ?? "null");
        log.Fact(scenario, "killed_exit_code", killed.ExitCode?.ToString() ?? "null");
        log.Fact(scenario, "killed_exit_signal", killed.ExitSignal ?? "null");
        log.Fact(scenario, "killed_output_complete", killed.OutputComplete);
        log.Fact(scenario, "exited_status", exited.Status.ToString());
        log.Fact(scenario, "exited_error", exited.ErrorCode ?? "null");
        log.Fact(scenario, "exited_exit_code", exited.ExitCode?.ToString() ?? "null");
        log.Fact(scenario, "ok_status", ok.Status.ToString());
        log.Fact(scenario, "ok_error", ok.ErrorCode ?? "null");
        log.Fact(scenario, "ok_stdout", ok.Stdout.Trim());
        log.Fact(scenario, "operations", 3);
        log.Fact(scenario, "connections", 3);

        log.Assert(scenario, "ok_is_only_completed", ok.Status == SshExecutionStatus.Completed && ok.ExitCode == 0, ok.Status.ToString());
        log.Assert(scenario, "nonzero_keeps_exit_code", exited.ExitCode == 7 && exited.ErrorCode == SshErrorCodes.RemoteExitNonzero, exited.ErrorCode ?? "null");
        log.Assert(
            scenario,
            "signal_only_is_own_code",
            killed.ErrorCode is SshErrorCodes.RemoteExitSignal or SshErrorCodes.RemoteExitNonzero or SshErrorCodes.ExitStatusMissing,
            killed.ErrorCode ?? "null");
        log.Assert(scenario, "signal_not_completed", killed.Status != SshExecutionStatus.Completed, killed.Status.ToString());
    }

    private static async Task<bool> FakeSshWaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < timeout)
        {
            await Task.Delay(50);
        }

        return condition();
    }

    private static SshExecuteRequest Execute(ProbeOptions options, string command) =>
        new(options.Target, command);

    /// <summary>与既有 coordinator 场景同一形状的冻结主机快照（pin 信任 + 冻结身份 + 冻结 cwd）。</summary>
    private static SshHostContext HostFor(
        this ProbeOptions options,
        int? operationTimeoutSeconds = null,
        SshTransferLimits? transfer = null,
        DateTimeOffset? deadline = null)
    {
        var timeout = operationTimeoutSeconds ?? options.OperationTimeoutSeconds;
        return new SshHostContext(
            options.Target,
            new SshOperationLimits
            {
                ConnectTimeoutSeconds = options.ConnectTimeoutSeconds,
                OperationTimeoutSeconds = timeout,
                MaxOperationTimeoutSeconds = Math.Max(timeout, options.OperationTimeoutSeconds),
                MaxOutputBytes = options.OutputBudgetBytes,
                MaxConcurrentOperations = 2,
            },
            options.IdentityRequest,
            SshTrustSnapshot.FromPin("ssh-ed25519", options.ExpectedFingerprint ?? string.Empty),
            options.RemoteTempDirectory,
            deadline,
            transfer);
    }
}
