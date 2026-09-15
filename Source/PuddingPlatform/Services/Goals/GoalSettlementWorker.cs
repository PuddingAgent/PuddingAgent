using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>Turn terminal fact 驱动的 bounded settlement/reconciliation worker。</summary>
public sealed class GoalSettlementWorker(
    GoalSettlementStore store,
    IGoalIterationVerifier verifier,
    GoalAcceptanceContractPlanner planner,
    GoalAcceptanceContractStore contractStore,
    IGoalCheckRunner checkRunner,
    GoalCheckRecordStore checkRecordStore,
    IOptions<GoalRunOptions> options,
    ILogger<GoalSettlementWorker> logger) : BackgroundService
{
    private readonly GoalRunOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ContinuationEnabled)
        {
            logger.LogInformation("[GoalSettlement] disabled");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
                await Task.Delay(_options.ContinuationScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GoalSettlement] scan failed");
                await Task.Delay(_options.ContinuationScanInterval, stoppingToken);
            }
        }
    }

    public async Task<int> ProcessOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.ContinuationEnabled)
            return 0;
        var candidates = await store.GetCandidatesAsync(_options.ContinuationBatchSize, ct);
        var applied = 0;
        foreach (var candidate in candidates)
        {
            // 候选级隔离：某个候选持续抛异常不得让整批后续目标结算饥饿（调用者取消仍原样上抛）。
            try
            {
                if (await SettleCandidateAsync(candidate, ct))
                    applied++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "[GoalSettlement] candidate settlement failed goal={GoalRunId} epoch={Epoch}",
                    candidate.GoalRunId,
                    candidate.ActivationEpoch);
            }
        }
        return applied;
    }

    /// <summary>结算单个候选；返回是否真正写入。任何非取消异常由调用方按候选隔离处理。</summary>
    private async Task<bool> SettleCandidateAsync(GoalSettlementCandidate candidate, CancellationToken ct)
    {
        var capsule = candidate.ToCapsule();

        // 空合同的有界派生：目标级条件来自 objective 显式证据声明；回归门禁只依据
        // 显式配置的受检目标生成；两者都为空则保持空合同（fail-closed）。
        if (capsule.Criteria.Count == 0)
        {
            var planned = await planner.EnsureContractAsync(
                candidate.GoalRunId,
                candidate.ActivationEpoch,
                capsule.ObjectiveVersion,
                candidate.PlanFingerprint,
                candidate.Objective,
                ct);

            if (planned)
            {
                var contract = await contractStore.LoadAsync(
                    candidate.GoalRunId,
                    candidate.ActivationEpoch,
                    capsule.ObjectiveVersion,
                    ct);
                capsule = capsule with
                {
                    Criteria = GoalVerificationPersistence.ReadCriteria(contract?.CriteriaJson),
                    Checks = GoalVerificationPersistence.ReadChecks(contract?.ChecksJson),
                };
            }
        }

        if (capsule.Checks.Count > 0)
        {
            var reported = await RunChecksAsync(candidate, capsule, ct);

            // 裁决依据必须是持久化的真实执行结果：用写回的 finished 报告刷新 capsule。
            var records = await checkRecordStore.ReadForEpochAsync(
                candidate.GoalRunId,
                candidate.ActivationEpoch,
                ct);
            var persisted = GoalVerificationPersistence.ReadReports(records);

            // waiting（超时/未结束进程）不是持久终态（存储层退回 pending 以便重新认领），
            // 因此不在持久集里。只喂持久集会让 typed wait 退化成“从未运行”（pending）。
            // 合并本轮报告，持久报告优先（旧结果可复用，不得被本轮覆盖）。
            var byCheckId = new Dictionary<string, GoalCheckReport>(StringComparer.Ordinal);
            foreach (var report in persisted)
                byCheckId[report.CheckId] = report;
            foreach (var report in reported)
            {
                // 只并入 waiting：超时/未结束进程按设计不构成持久终态（存储层退回 pending
                // 以便重新认领），若不一并喂给 verifier，typed wait 会退化成“从未运行”（pending）。
                // 其余状态（passed/failed）一律只认持久证据：执行器的自我声明不得成为裁决
                // 依据（负向对照测试锁死：自称 passed 但不落库 ⇒ 不得推进）。
                if (!string.Equals(report.Status, GoalCriterionResultStatuses.Waiting, StringComparison.Ordinal))
                    continue;
                byCheckId.TryAdd(report.CheckId, report);
            }

            capsule = capsule with { CheckReports = byCheckId.Values.ToList() };
        }

        var decision = await verifier.VerifyAsync(capsule, ct);
        if (!await store.ApplyAsync(candidate, decision, ct))
            return false;

        logger.LogInformation(
            "[GoalSettlement] applied goal={GoalRunId} epoch={Epoch} iteration={Iteration} turn={TurnId} verdict={Verdict}",
            candidate.GoalRunId,
            candidate.ActivationEpoch,
            candidate.IterationNo,
            candidate.TurnId,
            decision.Verdict);
        return true;
    }

    /// <summary>
    /// 结算前执行合同声明的受控检查（build/test/postcondition），把真实报告写回 goal_check_records。
    /// 不持有 SQLite 写事务等待进程；同一去重键的检查不会重复执行。
    /// </summary>
    private async Task<IReadOnlyList<GoalCheckReport>> RunChecksAsync(
        GoalSettlementCandidate candidate,
        GoalEvidenceCapsule capsule,
        CancellationToken ct)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(_options.CheckWorkingDirectory)
            ? null
            : _options.CheckWorkingDirectory.Trim();

        var context = new GoalCheckContext
        {
            GoalRunId = candidate.GoalRunId,
            ActivationEpoch = candidate.ActivationEpoch,
            WorkspaceId = candidate.WorkspaceId,
            AgentInstanceId = candidate.AgentInstanceId,
            IterationNo = candidate.IterationNo,
            Scope = capsule.VerificationScope,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = _options.CheckTimeoutSeconds,
        };

        try
        {
            return await checkRunner.RunAsync(capsule.Checks, context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 执行器故障不得伪装成通过：记录后照常裁决（无报告 => 未通过 => repair/等待）。
            logger.LogError(
                ex,
                "[GoalSettlement] check execution failed goal={GoalRunId} epoch={Epoch}",
                candidate.GoalRunId,
                candidate.ActivationEpoch);
            return [];
        }
    }
}
