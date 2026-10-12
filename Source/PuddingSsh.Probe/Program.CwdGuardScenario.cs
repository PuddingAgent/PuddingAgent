using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Transport;
using PuddingSsh.Validation;

namespace PuddingSsh.Probe;

/// <summary>
/// R1 的真实 shell 语义场景（ADR-096 D1）：cwd 守卫必须挡住**整个脚本**，而 cwd 正常时原脚本语义不变。
/// <para>
/// 纯测试只能断言字节精确串；「cd 失败后分号/换行/<c>||</c>/pipeline/后台列表都不会执行」必须由
/// 真实 POSIX shell 证明，所以放在探针里，并以后续一次独立执行核对 marker 文件是否被创建。
/// </para>
/// </summary>
internal static partial class Program
{
    private static async Task RunCwdGuardAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "cwd";
        var presentCwd = options.PresentCwd is { Length: > 0 } c ? c : "/tmp";
        var missingCwd = options.MissingCwd is { Length: > 0 } m ? m : "/__pudding_ssh_probe_guard_missing__";
        var quotedCwd = $"{presentCwd.TrimEnd('/')}/pudding cwd's";
        var runId = Guid.NewGuid().ToString("N")[..8];
        var factory = new SshNetTransportFactory();
        var operations = 0;
        var connections = 0;

        var markers = new List<(string Name, string Path)>();
        for (var i = 0; i < 6; i++)
        {
            markers.Add(($"case{i + 1}", $"{options.RemoteTempDirectory}/cwdguard-{runId}-{i + 1}.marker"));
        }

        // 0) 准备：建一个带空格与单引号的目录；清掉可能的残留 marker。
        await using (var setup = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            connections++;
            operations++;
            var prep = await setup.ExecuteAsync(
                $"mkdir -p \"{quotedCwd}\"; rm -f {string.Join(' ', markers.Select(x => x.Path))}; echo PREP_OK",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);

            log.Assert(scenario, "prepare_ok", prep.Stdout.Contains("PREP_OK", StringComparison.Ordinal), prep.Stdout.Trim());
            log.Fact(scenario, "quoted_cwd", quotedCwd);
        }

        // 1) cwd 不存在 × 六种复合形态：必须零输出、非零退出、零侧副作用。
        var missingCases = new (string Label, string Command)[]
        {
            ("semicolon", $"printf FIRST; touch {markers[0].Path}"),
            ("newline", $"printf FIRST\ntouch {markers[1].Path}"),
            ("or", $"printf FIRST || touch {markers[2].Path}"),
            ("pipeline", $"printf FIRST | tee {markers[3].Path}"),
            ("background", $"touch {markers[4].Path} & printf FIRST"),
            ("comment", $"printf FIRST # trailing comment; touch {markers[5].Path}"),
        };

        await using (var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            connections++;
            foreach (var (label, command) in missingCases)
            {
                operations++;
                var plan = SshPosixCommand.Compose(command, missingCwd, null);
                log.Fact(scenario, $"plan_{label}", plan.ExecutedCommand.Replace('\n', '⏎'));

                var result = await session.ExecuteAsync(
                    plan.ExecutedCommand,
                    TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                    options.OutputBudgetBytes,
                    CancellationToken.None);

                var stdout = result.Stdout.Trim();
                log.Fact(scenario, $"missing_{label}_stdout", stdout == string.Empty ? "<empty>" : stdout);
                log.Fact(scenario, $"missing_{label}_exit", result.ExitCode?.ToString() ?? "null");

                log.Assert(scenario, $"missing_{label}_no_output", stdout.Length == 0, stdout);
                log.Assert(
                    scenario,
                    $"missing_{label}_nonzero_exit",
                    result.ExitCode is not null and not 0,
                    result.ExitCode?.ToString() ?? "null");
            }
        }

        // 2) cwd 存在：原脚本语义必须逐条保留。
        await using (var session = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            connections++;
            async Task<SshExecuteResult> Run(string label, string command, string cwd)
            {
                operations++;
                var plan = SshPosixCommand.Compose(command, cwd, null);
                var result = await session.ExecuteAsync(
                    plan.ExecutedCommand,
                    TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                    options.OutputBudgetBytes,
                    CancellationToken.None);
                log.Fact(scenario, $"{label}_stdout", result.Stdout.Trim() == string.Empty ? "<empty>" : result.Stdout.Trim());
                log.Fact(scenario, $"{label}_exit", result.ExitCode?.ToString() ?? "null");
                return result;
            }

            var semicolon = await Run("ok_semicolon", "printf FIRST; printf SECOND", presentCwd);
            log.Assert(scenario, "ok_semicolon_output", semicolon.Stdout.Trim() == "FIRSTSECOND", semicolon.Stdout.Trim());
            log.Assert(scenario, "ok_semicolon_exit", semicolon.ExitCode == 0, semicolon.ExitCode?.ToString() ?? "null");

            var newline = await Run("ok_newline", "printf FIRST\nprintf SECOND", presentCwd);
            log.Assert(scenario, "ok_newline_output", newline.Stdout.Trim() == "FIRSTSECOND", newline.Stdout.Trim());

            var or = await Run("ok_or", "printf FIRST || printf SECOND", presentCwd);
            log.Assert(scenario, "ok_or_output", or.Stdout.Trim() == "FIRST", or.Stdout.Trim());

            var pipeline = await Run("ok_pipeline", "printf abc | tr a-z A-Z", presentCwd);
            log.Assert(scenario, "ok_pipeline_output", pipeline.Stdout.Trim() == "ABC", pipeline.Stdout.Trim());

            var heredoc = await Run("ok_heredoc", "cat <<'EOF'\nheredoc-ok\nEOF", presentCwd);
            log.Assert(scenario, "ok_heredoc_output", heredoc.Stdout.Contains("heredoc-ok", StringComparison.Ordinal), heredoc.Stdout.Trim());

            var background = await Run("ok_background", "printf BG & wait", presentCwd);
            log.Assert(scenario, "ok_background_output", background.Stdout.Contains("BG", StringComparison.Ordinal), background.Stdout.Trim());

            var quoted = await Run("ok_quoted_cwd", "pwd", quotedCwd);
            log.Assert(
                scenario,
                "ok_quoted_cwd_output",
                quoted.Stdout.Contains("pudding cwd's", StringComparison.Ordinal),
                quoted.Stdout.Trim());

            var exitCode = await Run("ok_exit_code", "exit 7", presentCwd);
            log.Assert(scenario, "ok_exit_code_preserved", exitCode.ExitCode == 7, exitCode.ExitCode?.ToString() ?? "null");
            log.Assert(
                scenario,
                "ok_exit_code_error",
                exitCode.ErrorCode == SshErrorCodes.RemoteExitNonzero,
                exitCode.ErrorCode ?? "null");
        }

        // 3) 侧副作用核对：六个 marker 一个都不许存在。
        await using (var check = await factory.ConnectAsync(
            ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
            CancellationToken.None))
        {
            connections++;
            operations++;
            var evidence = await check.ExecuteAsync(
                $"for f in {string.Join(' ', markers.Select(x => x.Path))}; do [ -e \"$f\" ] && printf 'PRESENT:%s\\n' \"$f\"; done; printf 'MARKERS_CHECKED\\n'",
                TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                options.OutputBudgetBytes,
                CancellationToken.None);

            var stdout = evidence.Stdout.Trim().Replace('\n', '|');
            log.Fact(scenario, "marker_evidence", stdout);
            log.Assert(
                scenario,
                "no_side_effects",
                !stdout.Contains("PRESENT", StringComparison.Ordinal) && stdout.Contains("MARKERS_CHECKED", StringComparison.Ordinal),
                stdout);
        }

        log.Fact(scenario, "operations", operations);
        log.Fact(scenario, "connections", connections);
        log.Fact(scenario, "verdict", "cwd guard holds for the whole script and preserves original semantics");
    }
}
