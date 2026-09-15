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

        // 空合同的有界派生：只依据显式配置的受检目标生成条件；未配置则保持空合同（fail-closed）。
        if (capsule.Criteria.Count == 0)
        {
            var planned = await planner.EnsureContractAsync(
                candidate.GoalRunId,
                candidate.ActivationEpoch,
                capsule.ObjectiveVersion,
                candidate.PlanFingerprint,
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
            await RunChecksAsync(candidate, capsule, ct);

            // 裁决依据必须是持久化的真实执行结果：用写回的 finished 报告刷新 capsule。
            var records = await checkRecordStore.ReadForEpochAsync(
                candidate.GoalRunId,
                candidate.ActivationEpoch,
                ct);
            capsule = capsule with { CheckReports = GoalVerificationPersistence.ReadReports(records) };
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
    private async Task RunChecksAsync(
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
            await checkRunner.RunAsync(capsule.Checks, context, ct);
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
        }
    }
}
