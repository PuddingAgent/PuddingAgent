using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// G3 首个 fail-closed verifier：只依赖 canonical Turn/Task facts。普通 Agent 文本中的
/// DONE 不会变成 complete；Task-bound Goal 只有在任务工具已把 Task 提交为 Completed
/// 后才能完成。后续模型 Verifier 可实现同一只读接口，但不能绕过这些确定性门禁。
/// </summary>
public sealed class ConservativeGoalIterationVerifier : IGoalIterationVerifier
{
    public Task<GoalVerificationDecision> VerifyAsync(
        GoalEvidenceCapsule capsule,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capsule);

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
        else if (string.Equals(capsule.TaskStatus, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Complete,
                Reason = "The bound Task has a canonical Completed fact submitted through the task state machine.",
                EvidenceRefs = capsule.EvidenceRefs,
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
        else
        {
            decision = new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = capsule.TaskId is null
                    ? "No independently verified completion fact exists; continue within the remaining budget."
                    : "The bound Task is not terminal; continue within the remaining budget.",
                EvidenceRefs = capsule.EvidenceRefs,
                NextAction = "Continue the next bounded Goal Iteration and produce canonical evidence.",
                UnmetCriteria = capsule.TaskAcceptanceCriteria is null
                    ? []
                    : [capsule.TaskAcceptanceCriteria],
            };
        }

        return Task.FromResult(decision);
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
