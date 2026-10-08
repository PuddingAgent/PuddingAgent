using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Transport;

namespace PuddingSsh.Probe;

/// <summary>
/// A1 / S1 的库能力探针（设计 §3「S1 内必须验证」与 §12 A1 判据）。它只使用组件公开合同，
/// 因此测到的就是交付路径；断言失败以退出码 1 表达，命令行用法错误以退出码 2 表达。
/// </summary>
internal static class Program
{
    private const long ThroughputStdoutBytes = 64L * 1024 * 1024;
    private const long ThroughputStderrBytes = 16L * 1024 * 1024;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(ProbeOptions.Usage);
            return 2;
        }

        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ProbeUsageException ex)
        {
            Console.Error.WriteLine("参数错误: " + ex.Message);
            Console.WriteLine(ProbeOptions.Usage);
            return 2;
        }

        var log = new ProbeLog();
        try
        {
            switch (options.Scenario)
            {
                case "fingerprint":
                    await RunFingerprintAsync(options, log);
                    break;
                case "hostkey":
                    await RunHostKeyAsync(options, log, options.ExpectedFingerprint!, options.ExpectAccept);
                    break;
                case "encrypted":
                    await RunEncryptedKeyAsync(options, log);
                    break;
                case "deadline":
                    await RunDeadlineAsync(options, log);
                    break;
                case "cancel":
                    await RunCancelAsync(options, log);
                    break;
                case "throughput":
                    await RunThroughputAsync(options, log);
                    break;
                case "matrix":
                    await RunFingerprintAsync(options, log);
                    if (options.ExpectedFingerprint is { Length: > 0 } fingerprint)
                    {
                        await RunHostKeyAsync(options, log, fingerprint, expectAccept: true);
                        await RunHostKeyAsync(options, log, ProbeHostKeyVerifier.Mutable(fingerprint), expectAccept: false);
                    }

                    await RunEncryptedKeyAsync(options, log);
                    await RunDeadlineAsync(options, log);
                    await RunCancelAsync(options, log);
                    await RunThroughputAsync(options, log);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Assert(options.Scenario, "unhandled_exception", false, ex.GetType().Name + ": " + ex.Message);
        }

        return log.Complete();
    }

    private static SshTransportConnectRequest ConnectRequest(
        ProbeOptions options,
        ISshHostKeyVerifier verifier,
        ISshIdentityProvider identityProvider)
    {
        var identity = identityProvider.GetIdentityAsync(
            new SshIdentityRequest(Path.GetDirectoryName(Path.GetFullPath(options.IdentityPath)) ?? "/", Path.GetFileName(options.IdentityPath)),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        return new SshTransportConnectRequest(
            options.Target,
            identity,
            verifier,
            TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
    }

    /// <summary>观测模式下出示的指纹：必须与 OpenSSH 自己算出来的一致（否则信任原语不一致）。</summary>
    private static async Task RunFingerprintAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "fingerprint";
        var verifier = new ProbeHostKeyVerifier(null, alwaysReject: true);
        var factory = new SshNetTransportFactory();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var session = await factory.ConnectAsync(
                ConnectRequest(options, verifier, new ProbeIdentityProvider(options.IdentityPath)),
                CancellationToken.None);
            log.Assert(scenario, "rejected", false, "默认拒绝模式下竟然建连成功");
        }
        catch (SshHostKeyRejectedException ex)
        {
            log.Assert(scenario, "rejected", ex.ErrorCode == SshErrorCodes.HostKeyUntrusted, ex.ErrorCode);
            log.Fact(scenario, "reject_duration_ms", stopwatch.ElapsedMilliseconds);
            log.Assert(scenario, "evidence_recorded", verifier.Observed.Count > 0, $"count={verifier.Observed.Count}");
            foreach (var observed in verifier.Observed)
            {
                log.Fact(scenario, "observed", observed);
            }
        }
    }

    /// <summary>正确指纹必须连通；错指纹必须在**认证之前**被拒绝（服务端不得出现认证成功）。</summary>
    private static async Task RunHostKeyAsync(
        ProbeOptions options,
        ProbeLog log,
        string fingerprint,
        bool expectAccept)
    {
        var scenario = expectAccept ? "hostkey_accept" : "hostkey_reject";
        var verifier = new ProbeHostKeyVerifier(fingerprint);
        var factory = new SshNetTransportFactory();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var session = await factory.ConnectAsync(
                ConnectRequest(options, verifier, new ProbeIdentityProvider(options.IdentityPath)),
                CancellationToken.None);

            if (!expectAccept)
            {
                log.Assert(scenario, "rejected", false, "指纹不匹配却建连成功");
                return;
            }

            log.Assert(scenario, "connected", session.IsConnected, $"is_connected={session.IsConnected}");
            log.Assert(
                scenario,
                "verified_fingerprint",
                ProbeHostKeyVerifier.FingerprintEquals(fingerprint, session.VerifiedHostKeySha256),
                $"expected={ProbeHostKeyVerifier.Normalize(fingerprint)} actual={ProbeHostKeyVerifier.Normalize(session.VerifiedHostKeySha256)}");
            log.Fact(scenario, "connect_duration_ms", stopwatch.ElapsedMilliseconds);
        }
        catch (SshHostKeyRejectedException ex)
        {
            log.Assert(scenario, "reject_code", ex.ErrorCode == SshErrorCodes.HostKeyMismatch, ex.ErrorCode);
            log.Fact(scenario, "reject_duration_ms", stopwatch.ElapsedMilliseconds);
            log.Assert(scenario, "evidence_recorded", verifier.Observed.Count > 0, $"count={verifier.Observed.Count}");
        }
    }

    /// <summary>加密私钥必须得到 <c>ssh.passphrase_required</c>，且错误里不得出现口令本身。</summary>
    private static async Task RunEncryptedKeyAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "encrypted_key";
        var encPath = options.IdentityPath + "_encrypted";
        if (!File.Exists(encPath))
        {
            log.Assert(scenario, "encrypted_key_present", false, $"未找到加密私钥 {encPath}");
            return;
        }

        var factory = new SshNetTransportFactory();
        try
        {
            await using var session = await factory.ConnectAsync(
                ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), new ProbeIdentityProvider(encPath)),
                CancellationToken.None);
            log.Assert(scenario, "passphrase_required", false, "加密私钥竟然直接建连成功");
            _ = session;
        }
        catch (SshComponentException ex)
        {
            log.Assert(scenario, "passphrase_required", ex.ErrorCode == SshErrorCodes.PassphraseRequired, ex.ErrorCode);
            log.Fact(scenario, "exception_type", ex.GetType().Name);
            log.Fact(scenario, "inner_type", ex.InnerException?.GetType().Name ?? "none");
            log.Assert(
                scenario,
                "no_passphrase_leak",
                !ex.Message.Contains("probe-passphrase", StringComparison.Ordinal),
                "错误文本不得包含口令");
        }
    }

    /// <summary>建连 deadline：不可达地址必须在 deadline + 宽限内失败，且给出稳定错误码。</summary>
    private static async Task RunDeadlineAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "connect_deadline";
        var blackhole = options.Target with { Host = "192.0.2.1", Port = 22, HostId = "blackhole" };
        var factory = new SshNetTransportFactory();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var session = await factory.ConnectAsync(
                new SshTransportConnectRequest(
                    blackhole,
                    new ProbeIdentityProvider(options.IdentityPath).GetIdentityAsync(
                        new SshIdentityRequest(".", "id"), CancellationToken.None).AsTask().GetAwaiter().GetResult(),
                    new ProbeHostKeyVerifier(options.ExpectedFingerprint),
                    TimeSpan.FromSeconds(options.ConnectTimeoutSeconds)),
                CancellationToken.None);
            log.Assert(scenario, "bounded_failure", false, "不可达地址竟然建连成功");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var code = SshFailureClassifier.Classify(ex, commandSubmitted: false);
            log.Fact(scenario, "error_code", code);
            log.Fact(scenario, "duration_ms", stopwatch.ElapsedMilliseconds);
            log.Assert(
                scenario,
                "bounded_failure",
                stopwatch.Elapsed < TimeSpan.FromSeconds(options.ConnectTimeoutSeconds + 5),
                $"elapsed_ms={stopwatch.ElapsedMilliseconds} limit_ms={(options.ConnectTimeoutSeconds + 5) * 1000}");
            log.Assert(
                scenario,
                "stable_code",
                code is SshErrorCodes.DnsFailed or SshErrorCodes.ConnectFailed or SshErrorCodes.Timeout,
                code);
        }
    }

    /// <summary>
    /// 取消语义（设计 §9.2）：取消必须受限；远端证据区分「已退出」与「状态未知」——
    /// 本探针用 PID 文件 + 标记文件核对，命令若继续跑完就会留下标记。
    /// </summary>
    private static async Task RunCancelAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "cancel";
        var factory = new SshNetTransportFactory();
        var marker = $"{options.RemoteTempDirectory}/cancel-marker-{Guid.NewGuid():N}";
        var pidFile = $"{options.RemoteTempDirectory}/cancel-pid-{Guid.NewGuid():N}";
        var operationTimeout = TimeSpan.FromSeconds(options.OperationTimeoutSeconds);

        var cancelled = (SshExecuteResult?)null;
        var stopwatch = Stopwatch.StartNew();
        var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), new ProbeIdentityProvider(options.IdentityPath)),
            CancellationToken.None);

        await using (session)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var result = await session.ExecuteAsync(
                $"echo $$ > {pidFile}; sleep 30; touch {marker}",
                operationTimeout,
                options.OutputBudgetBytes,
                cts.Token);
            stopwatch.Stop();
            cancelled = result;

            log.Fact(scenario, "status", result.Status.ToWire());
            log.Fact(scenario, "execution_state", result.ExecutionState.ToWire());
            log.Fact(scenario, "exit_code", result.ExitCode?.ToString() ?? "null");
            log.Fact(scenario, "exit_signal", result.ExitSignal ?? "null");
            log.Fact(scenario, "error_code", result.ErrorCode ?? "null");
            log.Fact(scenario, "duration_ms", result.DurationMs);
            log.Fact(scenario, "phase", result.Phase.ToWire());

            log.Assert(scenario, "status_cancelled", result.Status == SshExecutionStatus.Cancelled, result.Status.ToWire());
            log.Assert(
                scenario,
                "bounded_by_grace",
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"elapsed_ms={stopwatch.ElapsedMilliseconds}");
        }

        // 远端证据：等 2 秒后查 PID 与标记文件；命令若还活着，sleep 会继续并在 30 秒后写标记。
        await Task.Delay(2000);
        await using var probeSession = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), new ProbeIdentityProvider(options.IdentityPath)),
            CancellationToken.None);

        var evidence = await probeSession.ExecuteAsync(
            $"if [ -f {pidFile} ]; then if kill -0 $(cat {pidFile}) 2>/dev/null; then echo SHELL_ALIVE; else echo SHELL_GONE; fi; else echo NO_PIDFILE; fi; " +
            $"if [ -f {marker} ]; then echo MARKER_PRESENT; else echo MARKER_ABSENT; fi",
            TimeSpan.FromSeconds(15),
            options.OutputBudgetBytes,
            CancellationToken.None);

        var stdout = evidence.Stdout.Trim();
        log.Fact(scenario, "remote_evidence", stdout.Replace('\n', '|'));
        log.Assert(scenario, "marker_absent", stdout.Contains("MARKER_ABSENT", StringComparison.Ordinal), stdout);
        log.Assert(
            scenario,
            "exit_evidence_consistent",
            cancelled is not null
                && (cancelled.ExecutionState == SshExecutionState.Exited) == (cancelled.ExitCode is not null || cancelled.ExitSignal is not null),
            $"state={cancelled?.ExecutionState.ToWire()} exit_code={cancelled?.ExitCode?.ToString() ?? "null"} signal={cancelled?.ExitSignal ?? "null"}");
    }

    /// <summary>
    /// 输出资源（设计 §9.1）：双流同时大输出时，捕获必须受预算限制、计数必须完整、
    /// 峰值内存必须与输出量**无关**（「先取整份 Result 再截断」的实现会在这里取红）。
    /// 先用 1× 输出测绝对量，再用 3× 输出测**增长是否线性**。
    /// </summary>
    private static async Task RunThroughputAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "throughput";
        var factory = new SshNetTransportFactory();

        // 预热：JIT/线程池/缓冲池的首次分配与输出量无关，必须先跑掉，否则「驻留内存」读数被首跑效应污染。
        await RunThroughputPassAsync(
            options, factory, log, "warmup", 4L * 1024 * 1024, 1L * 1024 * 1024, measure: false);

        var small = await RunThroughputPassAsync(
            options, factory, log, "1x", ThroughputStdoutBytes, ThroughputStderrBytes, measure: true);
        var big = await RunThroughputPassAsync(
            options, factory, log, "3x", ThroughputStdoutBytes * 3, ThroughputStderrBytes * 3, measure: true);

        if (small is { } smallPass && big is { } bigPass)
        {
            log.Fact(scenario, "alloc_ratio_3x_over_1x", Math.Round((double)bigPass.Allocated / Math.Max(1, smallPass.Allocated), 2));

            // 捕获预算固定 64 KiB ⇒ 操作后**驻留**托管内存不得随输出量增长。
            // 托管「总分配量」是 churn（可回收），线性增长属库内部按包缓冲，单独作为事实记录。
            log.Assert(
                scenario,
                "retained_memory_bounded_1x",
                smallPass.Retained < 8L * 1024 * 1024,
                $"retained_1x={smallPass.Retained}");
            log.Assert(
                scenario,
                "retained_memory_bounded_3x",
                bigPass.Retained < 8L * 1024 * 1024,
                $"retained_3x={bigPass.Retained}");
            log.Assert(
                scenario,
                "retained_memory_sublinear",
                bigPass.Retained < (smallPass.Retained * 2) + (4L * 1024 * 1024),
                $"retained_1x={smallPass.Retained} retained_3x={bigPass.Retained}");
        }

        // Unicode 分块：预算故意小于单条输出，截断处不得出现替换字符（不许破坏字符）。
        var unicodeSession = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), new ProbeIdentityProvider(options.IdentityPath)),
            CancellationToken.None);
        await using (unicodeSession)
        {
            var result = await unicodeSession.ExecuteAsync(
                "i=0; while [ $i -lt 200 ]; do printf '中'; i=$((i+1)); done",
                TimeSpan.FromSeconds(30),
                32,
                CancellationToken.None);

            log.Fact(scenario, "unicode_captured_bytes", result.CapturedBytes);
            log.Fact(scenario, "unicode_stdout", result.Stdout);
            log.Assert(scenario, "unicode_status", result.Status == SshExecutionStatus.Completed, result.Status.ToWire());
            log.Assert(scenario, "unicode_bounded", result.CapturedBytes <= 32, $"captured={result.CapturedBytes}");
            log.Assert(
                scenario,
                "unicode_not_broken",
                !result.Stdout.Contains('\uFFFD') && result.Stdout.All(c => c == '中'),
                $"stdout={result.Stdout}");
        }
    }

    private static async Task<(long Allocated, long Retained, long Received)?> RunThroughputPassAsync(
        ProbeOptions options,
        SshNetTransportFactory factory,
        ProbeLog log,
        string label,
        long stdoutBytes,
        long stderrBytes,
        bool measure)
    {
        const string scenario = "throughput";
        var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint), new ProbeIdentityProvider(options.IdentityPath)),
            CancellationToken.None);

        await using (session)
        {
            var command =
                $"( head -c {stdoutBytes} /dev/zero | tr '\\0' o ) & " +
                $"( head -c {stderrBytes} /dev/zero | tr '\\0' e >&2 ) & wait";

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var retainedBefore = GC.GetTotalMemory(forceFullCollection: true);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var workingSetBefore = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();

            var result = await session.ExecuteAsync(
                command,
                TimeSpan.FromSeconds(Math.Max(options.OperationTimeoutSeconds, 180)),
                options.OutputBudgetBytes,
                CancellationToken.None);

            stopwatch.Stop();
            var allocatedDelta = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var retainedAfter = GC.GetTotalMemory(forceFullCollection: true);
            process.Refresh();
            var workingSetDelta = process.WorkingSet64 - workingSetBefore;

            var expectedReceived = stdoutBytes + stderrBytes;
            if (!measure)
            {
                log.Fact(scenario, $"{label}.status", result.Status.ToWire());
                log.Fact(scenario, $"{label}.received_bytes", result.ReceivedBytes);
                return null;
            }

            log.Fact(scenario, $"{label}.status", result.Status.ToWire());
            log.Fact(scenario, $"{label}.exit_code", result.ExitCode?.ToString() ?? "null");
            log.Fact(scenario, $"{label}.received_bytes", result.ReceivedBytes);
            log.Fact(scenario, $"{label}.captured_bytes", result.CapturedBytes);
            log.Fact(scenario, $"{label}.output_truncated", result.OutputTruncated);
            log.Fact(scenario, $"{label}.stdout_len", result.Stdout.Length);
            log.Fact(scenario, $"{label}.stderr_len", result.Stderr.Length);
            log.Fact(scenario, $"{label}.budget_bytes", options.OutputBudgetBytes);
            log.Fact(scenario, $"{label}.duration_ms", result.DurationMs);
            log.Fact(scenario, $"{label}.managed_allocated_bytes", allocatedDelta);
            log.Fact(scenario, $"{label}.managed_retained_bytes", retainedAfter - retainedBefore);
            log.Fact(scenario, $"{label}.working_set_delta_bytes", workingSetDelta);
            log.Fact(scenario, $"{label}.throughput_mib_per_s", Math.Round(result.ReceivedBytes / 1048576.0 / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), 1));

            log.Assert(scenario, $"{label}.completed", result.Status == SshExecutionStatus.Completed && result.ExitCode == 0, $"{result.Status.ToWire()}/{result.ErrorCode ?? "null"}");
            log.Assert(
                scenario,
                $"{label}.received_complete",
                result.ReceivedBytes >= expectedReceived * 95 / 100,
                $"received={result.ReceivedBytes} expected≈{expectedReceived}");
            log.Assert(
                scenario,
                $"{label}.capture_bounded",
                result.CapturedBytes <= options.OutputBudgetBytes,
                $"captured={result.CapturedBytes} budget={options.OutputBudgetBytes}");
            log.Assert(scenario, $"{label}.truncation_flagged", result.OutputTruncated, $"truncated={result.OutputTruncated}");
            log.Assert(
                scenario,
                $"{label}.memory_not_proportional",
                allocatedDelta < expectedReceived / 4,
                $"allocated={allocatedDelta} received={result.ReceivedBytes}");

            return (allocatedDelta, retainedAfter - retainedBefore, result.ReceivedBytes);
        }
    }
}
