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
        }

        // 以持久记录为准返回本 epoch 已完成的报告（含本轮复用既有结果的检查）。
        var finished = await recordStore.ReadForEpochAsync(context.GoalRunId, context.ActivationEpoch, ct);
        return GoalVerificationPersistence.ReadReports(finished);
    }

    private async Task<GoalCheckReport> ExecuteOneAsync(
        GoalCheckSpec spec,
        string checkRecordId,
        GoalCheckContext context,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.WorkingDirectory))
        {
            return FailedReport(
                spec,
                GoalCheckFailureCodes.EvidenceMissing,
                "No working directory was supplied for the bounded check; refusing to execute.");
        }

        if (!GoalCheckDefinitionRegistry.TryBuildCommand(spec, out var command, out var resolveFailure))
        {
            return FailedReport(
                spec,
                resolveFailure,
                "The check definition is not registered; arbitrary commands are not allowed.");
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

        var snapshot = await processManager.ReadOutputAsync(
            process.ProcessId,
            0,
            MaxCapturedLines,
            null,
            ct);
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
                $"Check exited with code {exitCode}.");
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
                    "No parseable test summary was produced; test evidence is not trustworthy.");
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
                    "The test run executed zero test cases.");
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
            if (info.Status is TerminalProcessStatus.Killed or TerminalProcessStatus.Failed)
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
        bool hasUnfinishedProcess = false) => new()
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
    };

    private enum ProcessWait
    {
        Exited,
        Killed,
        Timeout,
    }
}
