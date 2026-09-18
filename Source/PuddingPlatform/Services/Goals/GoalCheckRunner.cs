using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §5.3/§6.2：受控检查执行器（生产实现）。
/// <para>
/// 职责边界：只执行传入的 <see cref="GoalCheckSpec"/>，命令只能来自
/// <see cref="GoalCheckDefinitionRegistry"/> 的版本化定义；经既有 OS 级进程基座
/// <see cref="ITerminalProcessManager"/> 执行（与 terminal 工具同一后端，不新增自由 shell 旁路）；
/// 执行结果写入 goal_check_records（pending → leased → finished），报告携带真实
/// ExitCode / 执行测试数量 / 进程引用 / RunnerId / InvocationId。
/// </para>
/// <para>
/// 绝不伪造结果：未登记定义、无法解析测试数量、后台进程未结束都产生 failed/waiting 报告；
/// 调用者取消时记录回到 pending（不�24成 waiting）；超时产生 waiting（有租约回收作为恢复来源）。
/// </para>
/// </summary>
public sealed class GoalCheckRunner(
    GoalCheckRecordStore recordStore,
    ITerminalProcessManager processManager,
    ITerminalCommandAdmission admission,
    ILogger<GoalCheckRunner>? logger = null) : IGoalCheckRunner
{
    public const string RunnerId = "goal-check-runner";

    // 租约围栏：同一台机器上可能同时存在多个 runner 实例，只靠 "RunnerId:MachineName" 无法区分，
    // 过期租约被 B 重认领后 A 迟到的 Finish 仍会成功（真实结果被丢弃或提前落库）。实例级唯一后缀可挡住。
    private readonly string leaseOwnerToken = Guid.NewGuid().ToString("N")[..8];

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeaseSlack = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxCapturedLines = 400;

    /// <summary>
    /// 受控检查输出读取的字符预算。取 <c>TerminalProcess.ReadOutput</c> 的 maxChars 上限（200_000），
    /// 避开其默认值 20_000 —— 默认预算会在窗口<b>头部</b>截断（超出即 break），
    /// 导致末尾汇总行（dotnet test 的 summary / build 的收尾行）永远读不到。
    /// </summary>
    private const int MaxCapturedChars = 200_000;

    public async Task<IReadOnlyList<GoalCheckReport>> RunAsync(
        IReadOnlyList<GoalCheckSpec> checks,
        GoalCheckContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(context);
        if (checks.Count == 0)
            return [];

        var timeout = context.TimeoutSeconds is int seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;
        var leaseOwner = $"{RunnerId}:{Environment.MachineName}:{leaseOwnerToken}";

        await recordStore.EnqueueAsync(
            context.GoalRunId,
            context.ActivationEpoch,
            context.IterationNo,
            context.Scope,
            checks,
            ct);

        var leased = await recordStore.LeaseAsync(
            leaseOwner,
            context.GoalRunId,
            context.ActivationEpoch,
            timeout + LeaseSlack,
            checks.Count,
            ct);

        // 本轮实际产出的报告（含 waiting / 身份不一致等不会成为持久终态的报告）。
        var produced = new List<GoalCheckReport>();

        foreach (var record in leased)
        {
            var spec = checks.FirstOrDefault(item =>
                string.Equals(item.CheckId, record.CheckId, StringComparison.Ordinal));
            if (spec is null || !MatchesRecord(spec, record))
            {
                // 无匹配定义或身份不一致：如实记为 identity 失败，不臆造检查定义，也不记录成通过。
                await recordStore.FinishAsync(record.CheckRecordId, leaseOwner, OrphanReport(record), ct);
                continue;
            }

            GoalCheckReport report;
            try
            {
                report = await ExecuteOneAsync(spec, record.CheckRecordId, context, timeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用者取消：记录回到 pending，等待下一次认领 —— 不得吞成可自动恢复的 waiting。
                await recordStore.FinishAsync(
                    record.CheckRecordId,
                    leaseOwner,
                    PendingReport(spec, GoalCheckFailureCodes.CheckNotRun, "Check execution was cancelled by the caller."),
                    CancellationToken.None);
                throw;
            }

            await recordStore.FinishAsync(record.CheckRecordId, leaseOwner, report, ct);
            produced.Add(report);
        }

        // 以持久记录为准返回本 epoch 已完成的报告（含本轮复用既有结果的检查）。
        var finished = await recordStore.ReadForEpochAsync(context.GoalRunId, context.ActivationEpoch, ct);
        var persisted = GoalVerificationPersistence.ReadReports(finished);

        // waiting（超时 / 留下未结束后台进程）不构成持久终态：存储层把它退回 pending 以便重新认领，
        // 因此它不会出现在持久报告集里。若只返回持久集，调用方会把“依赖不可用的等待”
        // 误读成“从未运行”（真实运行复现：超时检查的 reports 为空，上层取 reports[0] 越界）。
        // 语义：持久报告优先（可复用旧结果），本轮新产生的报告按 CheckId 补齐。
        var merged = new List<GoalCheckReport>(persisted);
        var seen = new HashSet<string>(persisted.Select(item => item.CheckId), StringComparer.Ordinal);
        foreach (var item in produced)
        {
            if (seen.Add(item.CheckId))
                merged.Add(item);
        }

        return merged;
    }

    private async Task<GoalCheckReport> ExecuteOneAsync(
        GoalCheckSpec spec,
        string checkRecordId,
        GoalCheckContext context,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // G92-1 S1-c（片 3）：纯文本断言分支，必须在 WorkingDirectory 门禁之前分派——
        // 它不建命令、不经准入、不启进程、不需要工作目录；无 Workdir 环境也必须产出终态报告。
        if (string.Equals(spec.Kind, GoalVerificationSpecKinds.TextAssertion, StringComparison.Ordinal))
        {
            return EvaluateTextAssertion(spec, context);
        }

        if (string.IsNullOrWhiteSpace(context.WorkingDirectory))
        {
            return FailedReport(
                spec,
                GoalCheckFailureCodes.EvidenceMissing,
                "No working directory was supplied for the bounded check; refusing to execute.");
        }

        // file-evidence：只读核验（存在且非空）。与 build/test 同根（WorkingDirectory）、
        // 同租约/取消/超时生命周期（复用 Enqueue/Lease/Finish），但绝不构建命令、不经准入、
        // 不启动任何进程；不安全路径与不可读文件一律记为 failed（不是 pending）。
        if (string.Equals(spec.Kind, GoalVerificationSpecKinds.FileEvidence, StringComparison.Ordinal))
        {
            return EvaluateFileEvidence(spec, context);
        }

        if (!GoalCheckDefinitionRegistry.TryBuildCommand(spec, out var command, out var resolveFailure))
        {
            return FailedReport(
                spec,
                resolveFailure,
                "The check definition is not registered; arbitrary commands are not allowed.");
        }

        // ADR-092 §13.4：与 terminal 工具共用同一命令准入面（白名单 / 危险模式 / 宿主机安全不变量）。
        // 拒绝时如实记为 failed 且不启动任何进程——受控检查不得成为绕过准入的旁路。
        try
        {
            admission.EnsureAllowed(command, isYoloMode: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogWarning(
                "[GoalCheck] admission denied check={CheckId} command={Command}: {Reason}",
                spec.CheckId,
                command,
                ex.Message);
            return FailedReport(
                spec,
                GoalCheckFailureCodes.AdmissionDenied,
                $"Check command was denied by terminal admission policy: {ex.Message}");
        }

        var sessionId = string.IsNullOrWhiteSpace(context.SessionId)
            ? $"goal-check:{context.GoalRunId}"
            : context.SessionId;

        TerminalProcessInfo process;
        try
        {
            process = await processManager.StartAsync(sessionId, command, context.WorkingDirectory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "[GoalCheck] failed to start check {CheckId}", spec.CheckId);
            return FailedReport(spec, GoalCheckFailureCodes.CheckNotRun, $"Check process failed to start: {ex.Message}");
        }

        var exit = await WaitForExitAsync(process.ProcessId, sessionId, timeout, ct);
        if (exit == ProcessWait.Timeout)
        {
            // kill 失败时进程可能仍然存活：报告必须如实标注，不得声称"没有未结束的后台进程"。
            var killed = await TryKillAsync(process.ProcessId);
            return WaitingReport(
                spec,
                GoalCheckFailureCodes.CheckTimeout,
                $"Check exceeded its {timeout.TotalSeconds:0}s deadline; lease recovery will retry it.",
                process.ProcessId,
                hasUnfinishedProcess: !killed);
        }

        // 必须读尾部：dotnet test/build 的汇总行在输出末尾，全量输出远超 MaxCapturedLines；
        // 只读头部会把汇总行截掉，exitCode=0 也会恒报 test_count_unknown。
        //
        // 且必须给足字符预算并确认「窗口真的抵达末尾」：TerminalProcess.ReadOutput 的 maxChars 默认
        // 仅 20_000，且截断发生在窗口**头部**（超出预算即 break）。实测平台全量 dotnet test 输出 451 行，
        // 其中大量 NU1903 警告行约 10 万字符 ⇒ 只修正 offset 仍会返回窗口开头的一小段，永远到不了
        // 末尾汇总行。这正是 epoch1（未修）与 epoch5（含 46b9aa76 尾部 offset 修复后）同样复现
        // 「exitCode=0 却 test_count_unknown」的原因。
        // 故：显式给足 maxChars，并在实现报告 Truncated（窗口未抵达末尾）时按半窗口重试，
        // 直到抵达末尾或窗口缩到 1 行。未设置 Truncated 的桩不受影响（零行为变化）。
        var probe = await processManager.ReadOutputAsync(process.ProcessId, 0, 1, null, ct);
        var totalLines = probe?.TotalLines ?? 0;
        var window = MaxCapturedLines;
        var snapshot = await processManager.ReadOutputAsync(
            process.ProcessId,
            Math.Max(0, totalLines - window),
            window,
            MaxCapturedChars,
            ct);
        while (snapshot is { Truncated: true } && window > 1)
        {
            window = Math.Max(1, window / 2);
            snapshot = await processManager.ReadOutputAsync(
                process.ProcessId,
                Math.Max(0, totalLines - window),
                window,
                MaxCapturedChars,
                ct);
        }

        var lines = snapshot?.Lines ?? [];
        var exitCode = snapshot?.Process.ExitCode ?? process.ExitCode;
        var unfinished = exit == ProcessWait.Killed
            || GoalCheckOutputParser.HasUnfinishedProcessMarker(lines);

        if (unfinished)
        {
            return WaitingReport(
                spec,
                GoalCheckFailureCodes.CheckTimeout,
                "The check left an unfinished background process; evidence is not trustworthy yet.",
                process.ProcessId,
                hasUnfinishedProcess: true);
        }

        var summary = GoalCheckOutputParser.ParseTestSummary(lines);
        var reportRef = $"terminal-job:{process.ProcessId}";
        var evidenceRefs = new List<string> { reportRef };

        // fail-closed：进程已终态但退出码不可得（输出快照缺失 / 宿主信息丢失 / kill 失败进程仍存活）。
        // 宁可记 failed，绝不记 passed；也不得混同为等待 —— 明确失败码便于 triage。
        //
        // 边界（刻意不标记 EvidenceUnavailable）：退出码不可得是「平台没能给出结论」的宿主异常，
        // 平台有意让它成为终态以便 triage —— R4 回归锁
        // GoalCheckRunnerTests.Run_MissingExitCode_FailsClosedWithExplicitReason 明确要求
        // reports[0] = failed + exit_code_unknown，且 records[0].Status == finished（「也不当等待」）。
        // 可恢复回 pending 的语义只适用于「test 检查已执行但未产出工作单元证据」，见 LacksTestEvidence。
        if (exitCode is null)
        {
            return BuildReport(
                spec,
                GoalCriterionResultStatuses.Failed,
                evidenceRefs,
                reportRef,
                process.ProcessId,
                null,
                summary,
                GoalCheckFailureCodes.ExitCodeUnknown,
                "The process finished but its exit code was unavailable; recording failure (fail-closed).");
        }

        if (exitCode != 0)
        {
            return BuildReport(
                spec,
                GoalCriterionResultStatuses.Failed,
                evidenceRefs,
                reportRef,
                process.ProcessId,
                exitCode,
                summary,
                GoalCheckFailureCodes.NonZeroExitCode,
                $"Check exited with code {exitCode}.",
                // 既有 FailureCode 取值不变（下游依赖）；仅在 test 检查没有可用证据时补标记，
                // 让存储层把它视为可恢复失败回 pending，而不是终态（与 waiting 特判同构）。
                evidenceUnavailable: LacksTestEvidence(spec.Kind, summary));
        }

        if (string.Equals(spec.Kind, GoalVerificationSpecKinds.Test, StringComparison.Ordinal))
        {
            if (summary is null)
            {
                return BuildReport(
                    spec,
                    GoalCriterionResultStatuses.Failed,
                    evidenceRefs,
                    reportRef,
                    process.ProcessId,
                    exitCode,
                    null,
                    GoalCheckFailureCodes.TestCountUnknown,
                    "No parseable test summary was produced; test evidence is not trustworthy.",
                    evidenceUnavailable: true);
            }

            if (summary.ExecutedTestCount <= 0)
            {
                return BuildReport(
                    spec,
                    GoalCriterionResultStatuses.Failed,
                    evidenceRefs,
                    reportRef,
                    process.ProcessId,
                    exitCode,
                    summary,
                    GoalCheckFailureCodes.NoTestEvidence,
                    "The test run executed zero test cases.",
                    evidenceUnavailable: true);
            }

            if (spec.ExpectedTestCount is int expected && summary.ExecutedTestCount < expected)
            {
                return BuildReport(
                    spec,
                    GoalCriterionResultStatuses.Failed,
                    evidenceRefs,
                    reportRef,
                    process.ProcessId,
                    exitCode,
                    summary,
                    GoalCheckFailureCodes.TestCountUnknown,
                    $"Expected at least {expected} executed tests but observed {summary.ExecutedTestCount}.");
            }

            if (summary.FailedTestCount > 0)
            {
                return BuildReport(
                    spec,
                    GoalCriterionResultStatuses.Failed,
                    evidenceRefs,
                    reportRef,
                    process.ProcessId,
                    exitCode,
                    summary,
                    GoalCheckFailureCodes.TestsFailed,
                    $"{summary.FailedTestCount} test case(s) failed.");
            }
        }

        return BuildReport(
            spec,
            GoalCriterionResultStatuses.Passed,
            evidenceRefs,
            reportRef,
            process.ProcessId,
            exitCode,
            summary,
            null,
            null);
    }

    /// <summary>
    /// G92-1 S1-c（片 3）：text-assertion 只读判定——canonical Turn 终态的最终 assistant 输出
    /// 与 spec.ExpectedText 做 ordinal 精确比较；拒绝 contains、不 Trim、不做 Unicode 归一（D1）。
    /// 终态回复不可得 ⇒ 终态 failed（evidence_missing），绝不回 pending/waiting（否则检查永不收敛）。
    /// </summary>
    private static GoalCheckReport EvaluateTextAssertion(GoalCheckSpec spec, GoalCheckContext context)
    {
        var reply = context.FinalAssistantReply;
        if (reply is null)
        {
            return FailedReport(
                spec,
                GoalCheckFailureCodes.EvidenceMissing,
                "The canonical turn's final assistant output was unavailable; the text assertion cannot be evaluated (terminal failure, no retry).");
        }

        var evidenceRef = $"assistant-output:{reply.TurnId}@{reply.Sequence}";
        var matched = string.Equals(spec.ExpectedText, reply.Text, StringComparison.Ordinal);
        return BuildReport(
            spec,
            matched ? GoalCriterionResultStatuses.Passed : GoalCriterionResultStatuses.Failed,
            [evidenceRef],
            null,
            null,
            null,
            null,
            null,
            matched
                ? null
                : "The final assistant output does not exactly equal the expected text (ordinal; no trim, no contains).");
    }

    private async Task<ProcessWait> WaitForExitAsync(
        string processId,
        string sessionId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = processManager.ListProcesses(sessionId)
                .FirstOrDefault(item => string.Equals(item.ProcessId, processId, StringComparison.Ordinal));
            if (info is null)
                return ProcessWait.Timeout;
            if (info.Status == TerminalProcessStatus.Exited)
                return ProcessWait.Exited;
            // Failed（进程自行以非零退出码结束，TerminalProcessManager 在 Exited 事件里判定、ExitCode 已定）
            // 是「已结束且失败」，必须走 non_zero_exit_code 终态路径；只有 Killed（被终止，证据不完整）才落等待。
            // 两者混同会让跑完且失败的检查被误报成等待 → 持久层退回 pending → repair 分支永不可达。
            if (info.Status == TerminalProcessStatus.Failed)
                return ProcessWait.Failed;
            if (info.Status == TerminalProcessStatus.Killed)
                return ProcessWait.Killed;
            await Task.Delay(PollInterval, ct);
        }

        return ProcessWait.Timeout;
    }

    /// <summary>尽力终止检查进程；返回 false 表示进程可能仍然存活（调用方不得声称"没有未结束的后台进程"）。</summary>
    private async Task<bool> TryKillAsync(string processId)
    {
        try
        {
            return await processManager.KillAsync(processId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[GoalCheck] failed to kill check process {ProcessId}", processId);
            return false;
        }
    }

    /// <summary>工作项与检查定义必须身份一致（条件/版本/hash/指纹），否则不执行也不记成通过。</summary>
    private static bool MatchesRecord(GoalCheckSpec spec, GoalCheckRecordEntity record)
        => string.Equals(spec.CriterionId, record.CriterionId, StringComparison.Ordinal)
            && spec.CriterionRevision == record.CriterionRevision
            && string.Equals(spec.DefinitionHash, record.DefinitionHash, StringComparison.Ordinal)
            && string.Equals(spec.InputFingerprint, record.InputFingerprint, StringComparison.Ordinal);

    private static GoalCheckReport OrphanReport(GoalCheckRecordEntity record) => new()
    {
        CheckId = record.CheckId,
        CriterionId = record.CriterionId,
        CriterionRevision = record.CriterionRevision,
        Status = GoalCriterionResultStatuses.Failed,
        EvidenceRefs = [],
        InputFingerprint = record.InputFingerprint,
        DefinitionHash = record.DefinitionHash,
        RunnerId = RunnerId,
        ReportedAtUtc = DateTimeOffset.UtcNow,
        FailureCode = GoalCheckFailureCodes.CheckIdentityMismatch,
        Message = "No matching check spec/definition for this work item; refusing to execute or to record a pass.",
    };

    /// <summary>
    /// 只读核验 file-evidence：路径复用注册表安全校验（一票否决），解析必须落在受检工作区内；
    /// 文件存在且非空 → passed；不存在或为空 → failed（file_evidence_missing，不是 pending）；
    /// 无法读取 → failed（file_evidence_unreadable）并携带可读原因。只允许 File.Exists/读长度，
    /// 禁止执行任何 shell/进程/脚本。
    /// </summary>
    private static GoalCheckReport EvaluateFileEvidence(GoalCheckSpec spec, GoalCheckContext context)
    {
        if (spec.InputRefs.Count != 1 || string.IsNullOrWhiteSpace(spec.InputRefs[0]))
        {
            return FailedReport(
                spec,
                GoalCheckFailureCodes.InputFingerprintMissing,
                "File evidence check requires exactly one relative input path.");
        }

        var target = spec.InputRefs[0].Trim();
        if (!GoalCheckDefinitionRegistry.IsSafeEvidenceFilePath(target))
        {
            return FailedReport(
                spec,
                GoalCheckFailureCodes.UnsupportedCheckKind,
                $"Unsafe file evidence path refused (absolute/escape/wildcard/metachar): {target}");
        }

        // 与 build/test 同一执行根；二次解析验证防止规范化差异导致的逃逸。
        var root = Path.GetFullPath(context.WorkingDirectory!);
        var fullPath = Path.GetFullPath(Path.Combine(root, target));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return FailedReport(
                spec,
                GoalCheckFailureCodes.UnsupportedCheckKind,
                $"Resolved evidence path escapes the check working directory: {target}");
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                return FailedReport(
                    spec,
                    GoalCheckFailureCodes.FileEvidenceMissing,
                    $"Evidence file does not exist: {target}");
            }

            var length = new FileInfo(fullPath).Length;
            if (length <= 0)
            {
                return FailedReport(
                    spec,
                    GoalCheckFailureCodes.FileEvidenceMissing,
                    $"Evidence file exists but is empty: {target}");
            }

            return BuildReport(
                spec,
                GoalCriterionResultStatuses.Passed,
                [$"file:{target}"],
                $"file:{target}",
                null,
                null,
                null,
                null,
                $"Evidence file present and non-empty ({length} bytes): {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不吞异常冒充通过：读取失败如实记为 failed 并携带原因。
            return FailedReport(
                spec,
                GoalCheckFailureCodes.FileEvidenceUnreadable,
                $"Evidence file could not be read: {target} ({ex.Message})");
        }
    }

    private static GoalCheckReport PendingReport(
        GoalCheckSpec spec,
        string failureCode,
        string message) => BuildReport(
        spec,
        GoalCriterionResultStatuses.Pending,
        [],
        null,
        null,
        null,
        null,
        failureCode,
        message);

    private static GoalCheckReport FailedReport(
        GoalCheckSpec spec,
        string failureCode,
        string message) => BuildReport(
        spec,
        GoalCriterionResultStatuses.Failed,
        [],
        null,
        null,
        null,
        null,
        failureCode,
        message);

    private static GoalCheckReport WaitingReport(
        GoalCheckSpec spec,
        string failureCode,
        string message,
        string processId,
        bool hasUnfinishedProcess = false) => BuildReport(
        spec,
        GoalCriterionResultStatuses.Waiting,
        [$"terminal-job:{processId}"],
        $"terminal-job:{processId}",
        processId,
        null,
        null,
        failureCode,
        message,
        hasUnfinishedProcess);

    private static GoalCheckReport BuildReport(
        GoalCheckSpec spec,
        string status,
        IReadOnlyList<string> evidenceRefs,
        string? reportRef,
        string? processId,
        int? exitCode,
        GoalCheckOutputParser.TestSummary? summary,
        string? failureCode,
        string? message,
        bool hasUnfinishedProcess = false,
        bool evidenceUnavailable = false) => new()
    {
        CheckId = spec.CheckId,
        CriterionId = spec.CriterionId,
        CriterionRevision = spec.CriterionRevision,
        Status = status,
        EvidenceRefs = evidenceRefs,
        InputFingerprint = spec.InputFingerprint,
        DefinitionHash = spec.DefinitionHash,
        ReportRef = reportRef,
        ExitCode = exitCode,
        ExecutedTestCount = summary?.ExecutedTestCount,
        PassedTestCount = summary?.PassedTestCount,
        FailedTestCount = summary?.FailedTestCount,
        HasUnfinishedBackgroundProcess = hasUnfinishedProcess,
        RunnerId = RunnerId,
        InvocationId = processId,
        ReportedAtUtc = DateTimeOffset.UtcNow,
        FailureCode = failureCode,
        Message = message,
        EvidenceUnavailable = evidenceUnavailable,
    };

    /// <summary>
    /// test 检查是否未产出可用于判定的证据（无可解析汇总或执行数为零）。
    /// 这类失败多源于外部环境（构建/宿主抖动），属于可恢复失败：报告标记 EvidenceUnavailable 后，
    /// 存储层（GoalCheckRecordStore.FinishAsync）不落 finished、回 pending 重跑，
    /// 避免去重键在本 epoch 内永久固化无效证据。构建失败 / 有真实汇总的失败不在此列（真实判定，必须缓存）。
    /// 边界：本判定只作用于 test 检查的「无工作单元证据」。exit_code_unknown（宿主拿不到退出码）
    /// 属平台异常，仍落 finished 终态（R4 锁），刻意不在此列。
    /// </summary>
    private static bool LacksTestEvidence(
        string kind,
        GoalCheckOutputParser.TestSummary? summary)
        => string.Equals(kind, GoalVerificationSpecKinds.Test, StringComparison.Ordinal)
            && (summary is null || summary.ExecutedTestCount <= 0);

    private enum ProcessWait
    {
        Exited,

        /// <summary>被外部终止（如 KillAsync）：证据不完整 ⇒ 等待语义不变。</summary>
        Killed,

        /// <summary>进程自行以非零退出码结束 ⇒ 已结束且失败，走 non_zero_exit_code 终态。</summary>
        Failed,
        Timeout,
    }
}
