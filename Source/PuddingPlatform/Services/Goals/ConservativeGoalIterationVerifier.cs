using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// G92-1 fail-closed verifier：只依赖 canonical Turn/Task facts 与受控检查报告。
/// 普通 Agent 文本中的 DONE 不会变成 complete；Task 已 Completed 只算是“完成提议”，
/// 真正的完成要求全部必需条件（GoalCriterion）都有同版本且 passed 的检查结果。
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
        var evidence = GoalCheckEvidencePolicy.Evaluate(capsule.Checks, capsule.CheckReports);
        var results = evidence.ToCriterionResults();
        var probe = new GoalVerificationDecision
        {
            Verdict = GoalVerificationVerdict.Continue,
            Reason = "criteria probe",
            EvidenceRefs = capsule.EvidenceRefs,
            Criteria = criteria,
            CriterionResults = results,
        };
        var allRequiredPassed = GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(probe);
        var taskCompleted = string.Equals(capsule.TaskStatus, "Completed", StringComparison.OrdinalIgnoreCase);

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
                capsule);
        }
        else if (capsule.Checks.Count == 0)
        {
            // 有必需条件但没有版本化检查定义：先做有界规划把检查定义出来，不得靠 Task 状态完成。
            decision = Blocked(
                "check_contract_missing",
                "Required criteria exist but no versioned check definition was planned; define the bounded checks before completion can be claimed.",
                capsule);
        }
        else if (evidence.HasPendingChecks())
        {
            // ADR-092 §5.1 步骤 1：证据尚未齐全 → 登记等待，不生成修复轮、不关闭 Goal。
            decision = Blocked(
                "check_results_pending",
                "Declared verification checks have not produced results yet; wait for the check facts instead of completing.",
                capsule);
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
        else if (allRequiredPassed && taskCompleted)
        {
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Complete,
                Reason =
                    "All required criteria have passing verification results and the bound Task has a canonical Completed fact.",
                EvidenceRefs = capsule.EvidenceRefs,
            };
        }
        else if (allRequiredPassed)
        {
            // 必需条件已全部通过：当前单元可前进；整体目标是否达成由 goal 级 gate 决定。
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = "All required criteria for the current WorkUnit passed; advance to the next ready WorkUnit.",
                EvidenceRefs = capsule.EvidenceRefs,
                NextAction = "Advance to the next ready WorkUnit, then verify the goal-level criteria.",
            };
        }
        else if (evidence.HasFailedChecks())
        {
            decision = Blocked(
                "criterion_failed",
                "One or more required criteria failed their checks; repair the current WorkUnit with the failure evidence.",
                capsule) with
            {
                NextAction = "Fix the failing check in the current WorkUnit and re-run the same checks.",
            };
        }
        else
        {
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = taskCompleted
                    ? "The bound Task is completed, but the goal's required criteria have no passing verification; completion is only proposed."
                    : "The required criteria are not yet verified; continue in the current WorkUnit.",
                EvidenceRefs = capsule.EvidenceRefs,
                NextAction = "Produce the missing verification evidence for the required criteria in the current WorkUnit.",
                BlockerCode = "acceptance_not_verified",
                BlockerMessage = "Required criteria have no passing verification result yet.",
            };
        }

        return Task.FromResult(decision with
        {
            Criteria = criteria,
            CriterionResults = results,
        });
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
