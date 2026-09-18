using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// G92-1 fail-closed verifier：只依赖 canonical Turn/Task facts 与受控检查报告。
/// 普通 Agent 文本中的 DONE 不会变成 complete；Task 状态只作为否决项参与（G92-1 P2：
/// Blocked/NeedsReview/Failed/Cancelled 仍然阻断完成），真正的完成要求全部必需条件（GoalCriterion）
/// 都有同版本且 passed 的检查结果（S1-b：不再区分验证作用域、不数剩余 WorkUnit，
/// 完成权归验收合同；计划收敛度由结算层 ApplyBoundPlanGates 单独裁决）。
/// 空验收合同不得 vacuous pass，必须先经一个有界规划步骤产生合同；
/// 本 verifier 只读且不执行任何工具（实际检查由 IGoalCheckRunner 负责）。
/// </summary>
public sealed class ConservativeGoalIterationVerifier : IGoalIterationVerifier
{
    public Task<GoalVerificationDecision> VerifyAsync(
        GoalEvidenceCapsule capsule,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capsule);

        var criteria = capsule.Criteria;
        // G92-1 S1-c（片5）：text-assertion 的证据必须绑定本 canonical Turn 的终态 assistant 输出；
        // capsule.AssistantOutputTurnId 为 null（reply 不可得）时，policy 对该 kind fail-closed。
        var evidence = GoalCheckEvidencePolicy.Evaluate(
            capsule.Checks,
            capsule.CheckReports,
            capsule.AssistantOutputTurnId);
        var results = evidence.ToCriterionResults();

        // T5：未满足清单只从真实证据收集（归一后的受控检查报告 + 合同必需条件差集）；
        // 只填充数据，不参与任何判定分支（verdict 的选择保持原样）。
        var unmetCriteria = BuildUnmetCriteria(criteria, evidence);

        var probe = new GoalVerificationDecision
        {
            Verdict = GoalVerificationVerdict.Continue,
            Reason = "criteria probe",
            EvidenceRefs = capsule.EvidenceRefs,
            Criteria = criteria,
            CriterionResults = results,
        };
        var allRequiredPassed = GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(probe);

        // G92-1 P2：完成不再以 Task 已 canonical Completed 为前提——该前提与"唯一合法的 Task
        // 完成通道会释放本轮结算要重验的 reservation"互锁，使完成永不可达。Task 状态只在下方
        // 否决分支（Blocked/NeedsReview/Failed/Cancelled）参与判定；完成权归结算事务。

        GoalVerificationDecision decision;
        if (!capsule.EvidenceComplete || capsule.HasPendingExecutionFacts)
        {
            decision = Blocked(
                "evidence_incomplete",
                "Canonical execution evidence is incomplete or still has pending facts.",
                capsule);
        }
        else if (!string.Equals(capsule.TerminalKind, "completed", StringComparison.OrdinalIgnoreCase))
        {
            decision = Blocked(
                $"iteration_{capsule.TerminalKind}",
                $"Goal Iteration ended as {capsule.TerminalKind}; explicit recovery is required.",
                capsule);
        }
        else if (criteria.Count == 0)
        {
            // ADR-092 §4：空合同不得 vacuous pass；处置映射为 repair（留在当前单元做有界规划），不关闭 Goal。
            decision = Blocked(
                "acceptance_contract_missing",
                "No acceptance contract exists for this Goal; run one bounded planning step to derive required criteria before completion can be claimed.",
                capsule) with
            {
                NextAction = "In this WorkUnit, derive the versioned acceptance contract (required criteria + their check definitions) for the goal objective, then re-run verification.",
            };
        }
        else if (capsule.Checks.Count == 0)
        {
            // 有必需条件但没有版本化检查定义：先做有界规划把检查定义出来，不得靠 Task 状态完成。
            decision = Blocked(
                "check_contract_missing",
                "Required criteria exist but no versioned check definition was planned; define the bounded checks before completion can be claimed.",
                capsule) with
            {
                NextAction = "Declare the versioned check definitions (kind, definition hash, input refs/fingerprint, expected test count) for the existing required criteria.",
                UnmetCriteria = unmetCriteria,
            };
        }
        else if (evidence.HasFailedChecks())
        {
            // 失败优先于等待：任一必需检查已失败时必须先修复，不得用其他检查的等待拖延，
            // 也不能因为同一条件的另一个检查通过而前进。
            decision = Blocked(
                "criterion_failed",
                "One or more required criteria failed their checks; repair the current WorkUnit with the failure evidence.",
                capsule) with
            {
                NextAction = "Fix the failing check in the current WorkUnit and re-run the same checks.",
                UnmetCriteria = unmetCriteria,
            };
        }
        else if (evidence.HasPendingChecks())
        {
            // ADR-092 §5.1 步骤 1：证据尚未齐全 → 登记等待，不生成修复轮、不关闭 Goal。
            decision = Blocked(
                "check_results_pending",
                "Declared verification checks have not produced results yet; wait for the check facts instead of completing.",
                capsule) with
            {
                UnmetCriteria = unmetCriteria,
            };
        }
        else if (string.Equals(capsule.TaskStatus, "Blocked", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(capsule.TaskStatus, "NeedsReview", StringComparison.OrdinalIgnoreCase))
        {
            decision = Blocked(
                "task_blocked",
                "The bound Task requires user/reviewer action.",
                capsule);
        }
        else if (string.Equals(capsule.TaskStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(capsule.TaskStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            decision = Blocked(
                "task_terminal_without_completion",
                $"The bound Task is {capsule.TaskStatus}.",
                capsule);
        }
        else if (allRequiredPassed && capsule.IsEngineeringGatesOnly)
        {
            // G92-1 S1-a（ADR-092 决策 4/6）：合同覆盖门。纯工程门禁合同（bounded_planning，
            // objective 未声明任何目标级证据）即使全部必需条件通过也不得完成：build/test 绿
            // 不能兜底业务条件，必须先做一次有界合同整理，把 objective 的必要条件映射进合同。
            // 插入位置纪律：位于全部失败/等待/阻塞分支之后、Complete 分支之前，不得上移。
            decision = Blocked(
                "contract_coverage_insufficient",
                "The acceptance contract covers engineering gates only and does not map the objective's goal-level conditions; completion cannot be claimed from build/test evidence alone.",
                capsule) with
            {
                NextAction = "In this iteration, run one bounded contract refinement: derive the versioned goal-level criteria and their check definitions from the objective, then re-run verification.",
                UnmetCriteria = unmetCriteria,
            };
        }
        else if (allRequiredPassed)
        {
            // S1-b：唯一完成判据——全部必需条件同版本 passed。验证作用域与剩余 WorkUnit
            // 轴已删除；绑定计划未收敛时，ApplyBoundPlanGates 仍会把本裁决降级为 Continue
            // 并产出 Advance（计划闸与 Advance 记账不在本刀范围）。
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Complete,
                Reason = "All required criteria passed for this Goal.",
                EvidenceRefs = capsule.EvidenceRefs,
            };
        }
        else
        {
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = "The required criteria are not yet verified; continue in the current WorkUnit.",
                EvidenceRefs = capsule.EvidenceRefs,
                NextAction = "Produce the missing verification evidence for the required criteria in the current WorkUnit.",
                BlockerCode = "acceptance_not_verified",
                BlockerMessage = "Required criteria have no passing verification result yet.",
                UnmetCriteria = unmetCriteria,
            };
        }

        return Task.FromResult(decision with
        {
            Criteria = criteria,
            CriterionResults = results,
        });
    }

    /// <summary>
    /// 从真实证据收集未满足清单（只读、纯函数、不参与判定）：
    /// ① 优先取本轮归一后的受控检查报告中一切非 passed 的结果（含 failed/waiting/pending/invalidated），
    ///    每项形如 "&lt;criterionId&gt;: &lt;failureCode&gt; - &lt;message&gt;"，可追溯到具体检查报告；
    /// ② 其次取合同必需条件与已覆盖集合之差——声明了必需条件却没有版本化检查定义覆盖。
    /// 顺序跟随声明过的检查定义与合同条件顺序，稳定可复现；无未满足项时保持空列表（不是 null）。
    /// </summary>
    private static IReadOnlyList<string> BuildUnmetCriteria(
        IReadOnlyList<GoalCriterion> criteria,
        IReadOnlyList<GoalCheckReport> evidence)
    {
        List<string> unmet = [];
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var report in evidence)
        {
            covered.Add(report.CriterionId);
            if (string.Equals(report.Status, GoalCriterionResultStatuses.Passed, StringComparison.Ordinal))
                continue;

            var code = string.IsNullOrWhiteSpace(report.FailureCode) ? report.Status : report.FailureCode!;
            var message = string.IsNullOrWhiteSpace(report.Message) ? report.Status : report.Message!;
            unmet.Add($"{report.CriterionId}: {code} - {message}");
        }

        foreach (var criterion in criteria)
        {
            // covered.Add 返回 false 说明该必需条件已有检查报告覆盖（其结果已在上面逐报告列出）。
            if (criterion.Required && covered.Add(criterion.Id))
                unmet.Add($"{criterion.Id}: check_not_declared - Required criterion has no versioned check declared.");
        }

        return unmet;
    }

    private static GoalVerificationDecision Blocked(
        string code,
        string message,
        GoalEvidenceCapsule capsule)
        => new()
        {
            Verdict = GoalVerificationVerdict.Blocked,
            Reason = message,
            EvidenceRefs = capsule.EvidenceRefs,
            BlockerCode = code,
            BlockerMessage = message,
            NextAction = "Resolve the blocker, then explicitly resume the Goal.",
        };
}
