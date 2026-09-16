using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// G92-1 fail-closed verifier：只依赖 canonical Turn/Task facts 与受控检查报告。
/// 普通 Agent 文本中的 DONE 不会变成 complete；Task 状态只作为否决项参与（G92-1 P2：
/// Blocked/NeedsReview/Failed/Cancelled 仍然阻断完成），真正的完成要求全部必需条件（GoalCriterion）
/// 都有同版本且 passed 的检查结果，且本次裁决作用域已达 goal（无剩余 WorkUnit）。
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

        // ADR-092 §6.2：步骤与整体的验证作用域必须显式区分。
        // 独立 Goal（无 Task）的作用域就是 goal，因此必须有可达的完成路径；
        // Task-bound Goal 只有在已知没有剩余 WorkUnit（RemainingWorkUnits == 0）或显式声明 goal scope 时才能完成，
        // 未知一律保守为 work_unit：一个单元通过只能推进。
        var boundToTask = !string.IsNullOrEmpty(capsule.TaskId);
        var goalScope = !boundToTask
            || string.Equals(capsule.VerificationScope, GoalVerificationScopes.Goal, StringComparison.OrdinalIgnoreCase)
            || capsule.RemainingWorkUnits == 0;
        var taskCompleted = string.Equals(capsule.TaskStatus, "Completed", StringComparison.OrdinalIgnoreCase);
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
        else if (allRequiredPassed && goalScope)
        {
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Complete,
                Reason = boundToTask
                    ? "All goal-scope required criteria passed and the bound plan has no remaining WorkUnit."
                    : "All goal-scope required criteria passed for this standalone Goal.",
                // 作用域/剩余数的唯一事实来源是 capsule 与绑定计划的真实计数：裁决记录不重复携带，避免第二份真值。
                EvidenceRefs = capsule.EvidenceRefs,
            };
        }
        else if (goalScope && capsule.RemainingWorkUnits == 0)
        {
            // ADR-092 §13.1 / D2 第 2 条（G92-1 刀 C）：绑定计划已收敛（0 个 Running 且全部必需单元已验证完成），
            // 但整体（goal）必需条件尚无同版本 passed 证据——这是"待整体验证"的合法中间态：Goal/Plan 保持非终态，
            // 由结算生成 goal 作用域的续行补齐整体验证，绝不在此误判完成。
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = "The bound execution plan has converged, but the goal-scope required criteria have no passing verification yet; the goal stays open pending overall verification.",
                EvidenceRefs = capsule.EvidenceRefs,
                NextAction = "Run the goal-scope verification for the required criteria (same contract version), then re-settle so the goal can complete atomically.",
                BlockerCode = "acceptance_not_verified",
                BlockerMessage = "Goal-scope required criteria have no passing verification result yet.",
                UnmetCriteria = unmetCriteria,
            };
        }
        else if (allRequiredPassed)
        {
            // 步骤（work_unit）全部通过只能推进：整体目标是否达成由 goal 级条件决定，
            // 不能因为一个单元通过就宣布整个目标完成。
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = "All required criteria for the current WorkUnit passed; advance to the next ready WorkUnit.",
                EvidenceRefs = capsule.EvidenceRefs,
                NextAction = "Advance to the next ready WorkUnit, then verify the goal-level criteria.",
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
