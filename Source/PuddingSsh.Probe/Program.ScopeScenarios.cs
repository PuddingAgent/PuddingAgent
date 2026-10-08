using System.Diagnostics;
using System.Security.Cryptography;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using PuddingSsh.Transport;
using PuddingSsh.Validation;

namespace PuddingSsh.Probe;

/// <summary>
/// A4 的真实协议场景（设计 §6.1/§6.3/§9.2）：两类 `.ssh` 目录、known_hosts 端到端（撤销 / 换 key）、
/// 错误账号、命令执行中断线、服务端忽略 TERM。
/// <para>
/// 这些场景都在**真实 OpenSSH** 上跑，服务端侧的认证次数与客户端密钥指纹由运行器从 sshd 日志核对。
/// </para>
/// </summary>
internal static partial class Program
{
    /// <summary>known_hosts 端到端：accept 必须连通；revoked / rotated 必须被拒且不认证。</summary>
    private static async Task RunKnownHostsAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "knownhosts";
        if (options.KnownHostsPath is not { Length: > 0 } knownHostsPath)
        {
            log.Assert(scenario, "known_hosts_required", false, "本场景需要 --known-hosts");
            return;
        }

        var expectation = options.Expectation;
        SshTrustSnapshot trust;
        try
        {
            trust = SshTrustSnapshot.FromKnownHosts(File.ReadAllText(knownHostsPath));
        }
        catch (SshComponentException ex)
        {
            log.Assert(scenario, "known_hosts_parseable", false, ex.ErrorCode + ": " + ex.Message);
            return;
        }

        log.Fact(scenario, "expect", expectation);
        log.Fact(scenario, "trust_entries", trust.EntryCount);

        var factory = new SshNetTransportFactory();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var session = await factory.ConnectAsync(
                ConnectRequest(options, trust.CreateVerifier(options.Host, options.Port), options.IdentityRequest),
                CancellationToken.None);
            stopwatch.Stop();

            log.Fact(scenario, "verified_fingerprint", session.VerifiedHostKeySha256);
            log.Fact(scenario, "connect_duration_ms", stopwatch.ElapsedMilliseconds);

            if (expectation != "accept")
            {
                log.Assert(scenario, "rejected", false, $"期望 {expectation} 被拒，却建连成功");
                return;
            }

            var result = await session.ExecuteAsync(
                "echo known-hosts-ok",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);

            log.Fact(scenario, "execute_stdout", result.Stdout.Trim());
            log.Assert(scenario, "accepted_and_executed", result.Status == SshExecutionStatus.Completed && result.ExitCode == 0, result.Status.ToWire());
            log.Assert(scenario, "stdout_marker", result.Stdout.Contains("known-hosts-ok", StringComparison.Ordinal), result.Stdout.Trim());
        }
        catch (SshHostKeyRejectedException ex)
        {
            stopwatch.Stop();
            log.Fact(scenario, "reject_duration_ms", stopwatch.ElapsedMilliseconds);
            log.Fact(scenario, "reject_code", ex.ErrorCode);
            log.Assert(scenario, "rejected", expectation != "accept", $"期望 accept，却被拒：{ex.ErrorCode}");
            if (expectation == "revoked")
            {
                log.Assert(scenario, "revoked_code", ex.ErrorCode == SshErrorCodes.HostKeyRevoked, ex.ErrorCode);
            }
            else if (expectation == "rotated")
            {
                log.Assert(scenario, "mismatch_code", ex.ErrorCode == SshErrorCodes.HostKeyMismatch, ex.ErrorCode);
            }
        }
    }

    /// <summary>
    /// 两类 `.ssh` 目录（current_user 形态 / agent_private 形态）：
    /// 各自用自己的密钥与 known_hosts 都能工作；缺文件时**不回退**到另一个目录；组件**不写**这两个目录。
    /// </summary>
    private static async Task RunScopeDirsAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "dirs";
        var scopeA = options.ScopeADirectory;
        var scopeB = options.ScopeBDirectory;
        if (scopeA is not { Length: > 0 } || scopeB is not { Length: > 0 })
        {
            log.Assert(scenario, "scope_dirs_required", false, "本场景需要 --scope-a 与 --scope-b");
            return;
        }

        var beforeA = SnapshotDirectory(scopeA);
        var beforeB = SnapshotDirectory(scopeB);
        log.Fact(scenario, "scope_a_files", beforeA.Count);
        log.Fact(scenario, "scope_b_files", beforeB.Count);

        var factory = new SshNetTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new ProbeIdentityProvider());
        var trustA = SshTrustSnapshot.FromKnownHosts(File.ReadAllText(Path.Combine(scopeA, "known_hosts")));
        var trustB = SshTrustSnapshot.FromKnownHosts(File.ReadAllText(Path.Combine(scopeB, "known_hosts")));

        // 1) 目录 A（current_user 形态）：自己的密钥 + 自己的 known_hosts。
        var a = await ExecuteWithScopeAsync(
            options, coordinator, trustA, new SshIdentityRequest(scopeA, "id_ed25519"), "echo scope-a-ok", CancellationToken.None);
        log.Fact(scenario, "scope_a_status", a.Status.ToWire());
        log.Fact(scenario, "scope_a_stdout", a.Stdout.Trim());
        log.Assert(scenario, "scope_a_works", a.Status == SshExecutionStatus.Completed && a.Stdout.Contains("scope-a-ok", StringComparison.Ordinal), a.ErrorCode ?? a.Status.ToWire());

        // 2) 目录 B（agent_private 形态）：另一把密钥 + 自己的 known_hosts，必须同样可用。
        var b = await ExecuteWithScopeAsync(
            options, coordinator, trustB, new SshIdentityRequest(scopeB, "id_ed25519"), "echo scope-b-ok", CancellationToken.None);
        log.Fact(scenario, "scope_b_status", b.Status.ToWire());
        log.Fact(scenario, "scope_b_stdout", b.Stdout.Trim());
        log.Assert(scenario, "scope_b_works", b.Status == SshExecutionStatus.Completed && b.Stdout.Contains("scope-b-ok", StringComparison.Ordinal), b.ErrorCode ?? b.Status.ToWire());

        // 3) 目录 B 里**没有**这个文件名：必须 credential_unavailable，绝不回退到目录 A 的密钥。
        var missing = await ExecuteWithScopeAsync(
            options, coordinator, trustB, new SshIdentityRequest(scopeB, "id_ed25519_missing"), "echo must-not-run", CancellationToken.None);
        log.Fact(scenario, "missing_identity_code", missing.ErrorCode ?? "null");
        log.Fact(scenario, "missing_identity_phase", missing.Phase.ToWire());
        log.Assert(scenario, "no_identity_fallback", missing.ErrorCode == SshErrorCodes.CredentialUnavailable, missing.ErrorCode ?? "null");
        log.Assert(scenario, "missing_not_submitted", missing.ExecutionState == SshExecutionState.NotSubmitted, missing.ExecutionState.ToWire());

        // 4) 同一个目录 B，但信任集合记录的是**别的**主机公钥（换 key）⇒ mismatch。
        if (options.RotatedKnownHostsPath is { Length: > 0 } rotatedPath)
        {
            var rotatedTrust = SshTrustSnapshot.FromKnownHosts(File.ReadAllText(rotatedPath));
            var rotated = await ExecuteWithScopeAsync(
                options, coordinator, rotatedTrust, new SshIdentityRequest(scopeB, "id_ed25519"), "echo must-not-run", CancellationToken.None);
            log.Fact(scenario, "rotated_code", rotated.ErrorCode ?? "null");
            log.Assert(scenario, "rotated_rejected", rotated.ErrorCode == SshErrorCodes.HostKeyMismatch, rotated.ErrorCode ?? "null");
        }
        else
        {
            log.Assert(scenario, "rotated_known_hosts_provided", false, "需要 --rotated-known-hosts");
        }

        // 5) 组件只读：两个目录的内容必须逐字节不变。
        var afterA = SnapshotDirectory(scopeA);
        var afterB = SnapshotDirectory(scopeB);
        log.Assert(scenario, "scope_a_untouched", SnapshotsEqual(beforeA, afterA), DescribeDiff(beforeA, afterA));
        log.Assert(scenario, "scope_b_untouched", SnapshotsEqual(beforeB, afterB), DescribeDiff(beforeB, afterB));
    }

    /// <summary>
    /// 服务端忽略 TERM（设计 §9.2）：取消后**没有**退出码/信号证据 ⇒ <c>execution_state</c> 必须是 unknown，
    /// 且远端进程可能仍然活着。这正是「取消只是发送信号」的可证伪形式。
    /// </summary>
    private static async Task RunIgnoreTermAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "ignoreterm";
        var factory = new SshNetTransportFactory();
        var pidFile = $"{options.RemoteTempDirectory}/ignoreterm-pid-{Guid.NewGuid():N}";
        var marker = $"{options.RemoteTempDirectory}/ignoreterm-marker-{Guid.NewGuid():N}";

        var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), options.IdentityRequest),
            CancellationToken.None);

        SshExecuteResult? cancelled;
        await using (session)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            cancelled = await session.ExecuteAsync(
                $"trap '' TERM HUP; echo $$ > {pidFile}; sleep 30; touch {marker}",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                cts.Token);
        }

        log.Fact(scenario, "status", cancelled.Status.ToWire());
        log.Fact(scenario, "execution_state", cancelled.ExecutionState.ToWire());
        log.Fact(scenario, "exit_code", cancelled.ExitCode?.ToString() ?? "null");
        log.Fact(scenario, "exit_signal", cancelled.ExitSignal ?? "null");
        log.Fact(scenario, "duration_ms", cancelled.DurationMs);

        log.Assert(scenario, "cancelled", cancelled.Status == SshExecutionStatus.Cancelled, cancelled.Status.ToWire());
        log.Assert(
            scenario,
            "no_exit_evidence",
            cancelled.ExitCode is null && cancelled.ExitSignal is null,
            $"exit_code={cancelled.ExitCode?.ToString() ?? "null"} signal={cancelled.ExitSignal ?? "null"}");
        log.Assert(
            scenario,
            "state_unknown",
            cancelled.ExecutionState == SshExecutionState.Unknown,
            cancelled.ExecutionState.ToWire());

        // 远端证据：进程是否还活着？活着 ⇒ 组件「不声称远端已消失」是必须的，而不是保守。
        await Task.Delay(3000);
        await using var probeSession = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), options.IdentityRequest),
            CancellationToken.None);

        var evidence = await probeSession.ExecuteAsync(
            $"if [ -f {pidFile} ]; then if kill -0 $(cat {pidFile}) 2>/dev/null; then echo SHELL_ALIVE; else echo SHELL_GONE; fi; else echo NO_PIDFILE; fi; " +
            $"if [ -f {marker} ]; then echo MARKER_PRESENT; else echo MARKER_ABSENT; fi",
            TimeSpan.FromSeconds(15),
            options.OutputBudgetBytes,
            CancellationToken.None);

        var stdout = evidence.Stdout.Trim().Replace('\n', '|');
        log.Fact(scenario, "remote_evidence_after_cancel", stdout);
        log.Assert(
            scenario,
            "remote_survived_our_term",
            stdout.Contains("SHELL_ALIVE", StringComparison.Ordinal),
            stdout);

        // 清理：远端进程是我们自己起的，必须杀掉，别把夹具环境留脏。
        var cleanup = await probeSession.ExecuteAsync(
            $"if [ -f {pidFile} ]; then kill -9 $(cat {pidFile}) 2>/dev/null || true; fi; rm -f {pidFile} {marker}; echo CLEANED",
            TimeSpan.FromSeconds(15),
            options.OutputBudgetBytes,
            CancellationToken.None);
        log.Assert(scenario, "remote_cleanup", cleanup.Stdout.Contains("CLEANED", StringComparison.Ordinal), cleanup.Stdout.Trim());
    }

    /// <summary>
    /// 命令执行中连接被服务端切断：必须是 <c>connection_lost</c>、状态未知、且**绝不自动重放**。
    /// 手段：让会话进程杀掉自己（<c>$PPID</c> 就是该连接的 sshd 会话进程），TCP 随即断开。
    /// </summary>
    private static async Task RunDisconnectAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "disconnect";
        var factory = new SshNetTransportFactory();
        var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), options.IdentityRequest),
            CancellationToken.None);

        await using (session)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await session.ExecuteAsync(
                "echo started; kill -9 $PPID; sleep 30",
                TimeSpan.FromSeconds(30),
                options.OutputBudgetBytes,
                CancellationToken.None);
            stopwatch.Stop();

            log.Fact(scenario, "status", result.Status.ToWire());
            log.Fact(scenario, "execution_state", result.ExecutionState.ToWire());
            log.Fact(scenario, "error_code", result.ErrorCode ?? "null");
            log.Fact(scenario, "retry_safe", result.RetrySafe);
            log.Fact(scenario, "duration_ms", result.DurationMs);
            log.Fact(scenario, "stdout_before_break", result.Stdout.Trim());

            log.Assert(
                scenario,
                "reports_connection_lost",
                result.ErrorCode == SshErrorCodes.ConnectionLost,
                result.ErrorCode ?? "null");
            log.Assert(
                scenario,
                "state_unknown",
                result.ExecutionState == SshExecutionState.Unknown,
                result.ExecutionState.ToWire());
            log.Assert(scenario, "never_replay", !result.RetrySafe, "命令已提交后断线不得标记为可重试");
            log.Assert(scenario, "bounded", stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"elapsed_ms={stopwatch.ElapsedMilliseconds}");
        }
    }

    /// <summary>错误账号：必须 <c>authentication_failed</c>，且不会有任何命令提交。</summary>
    private static async Task RunAuthFailureAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "auth";
        var factory = new SshNetTransportFactory();
        var identities = new ProbeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(factory, identities);

        var host = new SshHostContext(
            options.Target,
            new SshOperationLimits
            {
                ConnectTimeoutSeconds = options.ConnectTimeoutSeconds,
                OperationTimeoutSeconds = options.OperationTimeoutSeconds,
                MaxOutputBytes = options.OutputBudgetBytes,
                MaxConcurrentOperations = 2,
            },
            options.IdentityRequest,
            SshTrustSnapshot.FromPin("ssh-ed25519", options.ExpectedFingerprint ?? string.Empty));

        var result = await coordinator.ExecuteAsync(host, new SshExecuteRequest(options.Target, "echo must-not-run"), CancellationToken.None);

        log.Fact(scenario, "status", result.Status.ToWire());
        log.Fact(scenario, "error_code", result.ErrorCode ?? "null");
        log.Fact(scenario, "phase", result.Phase.ToWire());
        log.Fact(scenario, "username", options.Username);

        log.Assert(scenario, "authentication_failed", result.ErrorCode == SshErrorCodes.AuthenticationFailed, result.ErrorCode ?? "null");
        log.Assert(scenario, "not_submitted", result.ExecutionState == SshExecutionState.NotSubmitted, result.ExecutionState.ToWire());
        log.Assert(scenario, "not_retry_safe", !result.RetrySafe, "认证失败需要用户处理");
    }

    /// <summary>
    /// 经协调器按「一个作用域 = 一个冻结上下文」执行：身份与信任都由调用方给定，
    /// 因此「用哪把密钥、信哪份 known_hosts」在组件侧完全由这份上下文决定，不存在搜索或回退。
    /// </summary>
    private static async Task<SshExecuteResult> ExecuteWithScopeAsync(
        ProbeOptions options,
        SshOperationCoordinator coordinator,
        SshTrustSnapshot trust,
        SshIdentityRequest identity,
        string command,
        CancellationToken cancellationToken)
    {
        var host = new SshHostContext(
            options.Target,
            new SshOperationLimits
            {
                ConnectTimeoutSeconds = options.ConnectTimeoutSeconds,
                OperationTimeoutSeconds = options.OperationTimeoutSeconds,
                MaxOutputBytes = options.OutputBudgetBytes,
                MaxConcurrentOperations = 2,
            },
            identity,
            trust);

        return await coordinator.ExecuteAsync(
            host,
            new SshExecuteRequest(options.Target, command),
            cancellationToken);
    }

    private static SortedDictionary<string, string> SnapshotDirectory(string directory)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            snapshot[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return snapshot;
    }

    private static bool SnapshotsEqual(
        SortedDictionary<string, string> before,
        SortedDictionary<string, string> after) =>
        before.Count == after.Count && before.All(kv => after.TryGetValue(kv.Key, out var hash) && hash == kv.Value);

    private static string DescribeDiff(
        SortedDictionary<string, string> before,
        SortedDictionary<string, string> after) =>
        $"before=[{string.Join(',', before.Keys)}] after=[{string.Join(',', after.Keys)}]";
}
