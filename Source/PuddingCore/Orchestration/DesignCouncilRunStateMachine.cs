namespace PuddingCode.Orchestration;

/// <summary>
/// Pure state machine for a compiled design-council plan. It owns activation, claims, gates,
/// pause/resume, quorum failure, and cancellation, but performs no persistence or child execution.
/// </summary>
public sealed class DesignCouncilRunStateMachine
{
    public SubAgentOrchestrationRunSnapshot CreateRun(
        SubAgentOrchestrationPlan plan,
        string runId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (plan.Status != SubAgentOrchestrationPlanStatus.Draft)
            throw new ArgumentException("Only a Draft orchestration plan can create a run.", nameof(plan));
        if (!plan.RequiresExplicitActivation)
            throw new ArgumentException("The orchestration plan must require explicit activation.", nameof(plan));
        if (plan.AllowFallback)
            throw new ArgumentException("MOA orchestration runs cannot allow model fallback.", nameof(plan));
        if (plan.Stages.Count == 0 || plan.WorkItems.Count == 0)
            throw new ArgumentException("The orchestration plan must contain stages and work items.", nameof(plan));

        EnsureUniqueIds(plan.Stages.Select(stage => stage.StageId), "stage", nameof(plan));
        EnsureUniqueIds(plan.WorkItems.Select(item => item.WorkItemId), "work item", nameof(plan));

        return new SubAgentOrchestrationRunSnapshot
        {
            RunId = runId.Trim(),
            Plan = plan,
            Status = SubAgentOrchestrationPlanStatus.Draft,
            Stages = Array.AsReadOnly(plan.Stages
                .Select(stage => new SubAgentOrchestrationStageState { StageId = stage.StageId })
                .ToArray()),
            WorkItems = Array.AsReadOnly(plan.WorkItems
                .Select(item => new SubAgentOrchestrationWorkItemState { WorkItemId = item.WorkItemId })
                .ToArray()),
            ContextResolutions = Array.Empty<SubAgentOrchestrationContextResolution>(),
            BlockingQuestions = Array.Empty<string>(),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public SubAgentOrchestrationTransitionResult Activate(
        SubAgentOrchestrationRunSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != SubAgentOrchestrationPlanStatus.Draft)
            return Reject(snapshot, "activation.invalid_status", $"Run status must be Draft, not {snapshot.Status}.");

        var firstStage = snapshot.Plan.Stages.OrderBy(stage => stage.Order).First();
        var activated = OpenStage(
            snapshot with
            {
                Status = SubAgentOrchestrationPlanStatus.Active,
                CurrentStageId = firstStage.StageId,
                ActivatedAtUtc = nowUtc
            },
            firstStage,
            nowUtc);

        return Accept(Touch(activated, snapshot.Version, nowUtc));
    }

    public SubAgentOrchestrationClaimResult ClaimReadyWork(
        SubAgentOrchestrationRunSnapshot snapshot,
        int requestedCount,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != SubAgentOrchestrationPlanStatus.Active)
            return RejectClaim(snapshot, "claim.run_not_active", $"Run status must be Active, not {snapshot.Status}.");
        if (requestedCount < 1)
            return RejectClaim(snapshot, "claim.count_invalid", "Requested claim count must be positive.");
        if (string.IsNullOrWhiteSpace(snapshot.CurrentStageId))
            return RejectClaim(snapshot, "claim.stage_missing", "Active run has no current stage.");

        var runningCount = snapshot.WorkItems.Count(item => item.Status == SubAgentOrchestrationWorkItemStatus.Running);
        var availableSlots = Math.Max(0, snapshot.Plan.MaxConcurrency - runningCount);
        var take = Math.Min(requestedCount, availableSlots);
        if (take == 0)
            return AcceptClaim(snapshot, Array.Empty<SubAgentOrchestrationClaimedWorkItem>());

        var definitions = snapshot.Plan.WorkItems.ToDictionary(item => item.WorkItemId, StringComparer.Ordinal);
        var ready = snapshot.WorkItems
            .Where(item => item.Status == SubAgentOrchestrationWorkItemStatus.Ready)
            .Where(item => string.Equals(definitions[item.WorkItemId].StageId, snapshot.CurrentStageId, StringComparison.Ordinal))
            .Take(take)
            .ToArray();
        if (ready.Length == 0)
            return AcceptClaim(snapshot, Array.Empty<SubAgentOrchestrationClaimedWorkItem>());

        var claimedIds = ready.Select(item => item.WorkItemId).ToHashSet(StringComparer.Ordinal);
        var claimMap = ready.ToDictionary(
            item => item.WorkItemId,
            item => $"{item.WorkItemId}/claim/{item.Attempt + 1:D2}",
            StringComparer.Ordinal);
        var workItems = snapshot.WorkItems.Select(item =>
        {
            if (!claimedIds.Contains(item.WorkItemId))
                return item;

            return item with
            {
                Status = SubAgentOrchestrationWorkItemStatus.Running,
                Attempt = item.Attempt + 1,
                ClaimId = claimMap[item.WorkItemId],
                ClaimedAtUtc = nowUtc
            };
        }).ToArray();
        var stages = snapshot.Stages.Select(stage =>
            string.Equals(stage.StageId, snapshot.CurrentStageId, StringComparison.Ordinal)
                ? stage with { Status = SubAgentOrchestrationStageStatus.Running }
                : stage).ToArray();
        var updated = Touch(snapshot with
        {
            WorkItems = Array.AsReadOnly(workItems),
            Stages = Array.AsReadOnly(stages)
        }, snapshot.Version, nowUtc);
        var claims = ready.Select(item => new SubAgentOrchestrationClaimedWorkItem
        {
            ClaimId = claimMap[item.WorkItemId],
            WorkItem = definitions[item.WorkItemId]
        }).ToArray();

        return AcceptClaim(updated, Array.AsReadOnly(claims));
    }

    public SubAgentOrchestrationTransitionResult RecordCompletion(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentWorkItemCompletion completion,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(completion);
        if (snapshot.Status is not (SubAgentOrchestrationPlanStatus.Active or SubAgentOrchestrationPlanStatus.AwaitingUserInput))
            return Reject(snapshot, "completion.run_not_accepting_results", $"Run status {snapshot.Status} does not accept work results.");

        var state = snapshot.WorkItems.FirstOrDefault(item => string.Equals(item.WorkItemId, completion.WorkItemId, StringComparison.Ordinal));
        if (state is null)
            return Reject(snapshot, "completion.work_item_not_found", $"Work item '{completion.WorkItemId}' was not found.");
        if (state.Status != SubAgentOrchestrationWorkItemStatus.Running)
            return Reject(snapshot, "completion.work_item_not_running", $"Work item '{completion.WorkItemId}' is {state.Status}, not Running.");
        if (!string.Equals(state.ClaimId, completion.ClaimId, StringComparison.Ordinal))
            return Reject(snapshot, "completion.claim_mismatch", $"Claim '{completion.ClaimId}' does not own work item '{completion.WorkItemId}'.");
        if (completion.Outcome == SubAgentWorkItemOutcome.Succeeded
            && string.IsNullOrWhiteSpace(completion.Summary)
            && string.IsNullOrWhiteSpace(completion.OutputText)
            && string.IsNullOrWhiteSpace(completion.OutputReference))
        {
            return Reject(snapshot, "completion.output_required", "A successful completion requires output text, a summary, or an output reference.");
        }
        if (completion.Outcome == SubAgentWorkItemOutcome.Failed && string.IsNullOrWhiteSpace(completion.Error))
            return Reject(snapshot, "completion.error_required", "A failed completion requires an error.");
        if (completion.Outcome == SubAgentWorkItemOutcome.Failed && completion.RequiresUserInput)
            return Reject(snapshot, "completion.failed_cannot_request_input", "A failed completion cannot also request user input.");
        if (completion.RequiresUserInput && !HasText(completion.BlockingQuestions))
            return Reject(snapshot, "completion.blocking_questions_required", "RequiresUserInput needs at least one blocking question.");

        var contextGaps = SnapshotText(completion.ContextGaps);
        var blockingQuestions = SnapshotText(completion.BlockingQuestions);
        var terminalStatus = completion.Outcome == SubAgentWorkItemOutcome.Succeeded
            ? SubAgentOrchestrationWorkItemStatus.Succeeded
            : SubAgentOrchestrationWorkItemStatus.Failed;
        var workItems = snapshot.WorkItems.Select(item =>
            string.Equals(item.WorkItemId, completion.WorkItemId, StringComparison.Ordinal)
                ? item with
                {
                    Status = terminalStatus,
                    ExternalRunId = Normalize(completion.ExternalRunId),
                    ExternalSubSessionId = Normalize(completion.ExternalSubSessionId),
                    CompletedAtUtc = nowUtc,
                    Summary = Normalize(completion.Summary),
                    OutputText = Normalize(completion.OutputText),
                    OutputReference = Normalize(completion.OutputReference),
                    Error = Normalize(completion.Error),
                    ContextGaps = contextGaps,
                    RequiresUserInput = completion.RequiresUserInput,
                    BlockingQuestions = blockingQuestions
                }
                : item).ToArray();
        var updated = snapshot with { WorkItems = Array.AsReadOnly(workItems) };

        if (completion.RequiresUserInput)
        {
            updated = updated with
            {
                Status = SubAgentOrchestrationPlanStatus.AwaitingUserInput,
                PauseReason = $"context_input_required:{completion.WorkItemId}",
                BlockingQuestions = MergeText(snapshot.BlockingQuestions, blockingQuestions)
            };
        }
        else if (snapshot.Status == SubAgentOrchestrationPlanStatus.Active)
        {
            updated = EvaluateCurrentStage(updated, nowUtc);
        }

        return Accept(Touch(updated, snapshot.Version, nowUtc));
    }

    public SubAgentOrchestrationTransitionResult Resume(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationContextResolution resolution,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolution);
        if (snapshot.Status != SubAgentOrchestrationPlanStatus.AwaitingUserInput)
            return Reject(snapshot, "resume.run_not_waiting", $"Run status must be AwaitingUserInput, not {snapshot.Status}.");
        if (string.IsNullOrWhiteSpace(resolution.ResolutionId) || string.IsNullOrWhiteSpace(resolution.ProvidedBy) || string.IsNullOrWhiteSpace(resolution.Response))
            return Reject(snapshot, "resume.resolution_invalid", "ResolutionId, ProvidedBy, and Response are required.");
        if (snapshot.ContextResolutions.Any(item => string.Equals(item.ResolutionId, resolution.ResolutionId, StringComparison.OrdinalIgnoreCase)))
            return Reject(snapshot, "resume.resolution_duplicate", $"Resolution '{resolution.ResolutionId}' already exists.");

        var resolutions = snapshot.ContextResolutions.Append(resolution with
        {
            ResolutionId = resolution.ResolutionId.Trim(),
            ProvidedBy = resolution.ProvidedBy.Trim(),
            Response = resolution.Response.Trim(),
            CreatedAtUtc = nowUtc
        }).ToArray();
        var resumed = snapshot with
        {
            Status = SubAgentOrchestrationPlanStatus.Active,
            PauseReason = null,
            BlockingQuestions = Array.Empty<string>(),
            ContextResolutions = Array.AsReadOnly(resolutions)
        };
        resumed = EvaluateCurrentStage(resumed, nowUtc);

        return Accept(Touch(resumed, snapshot.Version, nowUtc));
    }

    public SubAgentOrchestrationTransitionResult Cancel(
        SubAgentOrchestrationRunSnapshot snapshot,
        string reason,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status is SubAgentOrchestrationPlanStatus.Completed or SubAgentOrchestrationPlanStatus.Failed or SubAgentOrchestrationPlanStatus.Cancelled)
            return Reject(snapshot, "cancel.run_terminal", $"Run status {snapshot.Status} is already terminal.");
        if (string.IsNullOrWhiteSpace(reason))
            return Reject(snapshot, "cancel.reason_required", "Cancellation reason is required.");

        var workItems = snapshot.WorkItems.Select(item => IsTerminal(item.Status)
            ? item
            : item with
            {
                Status = SubAgentOrchestrationWorkItemStatus.Cancelled,
                CompletedAtUtc = nowUtc,
                Error = reason.Trim()
            }).ToArray();
        var stages = snapshot.Stages.Select(stage => IsTerminal(stage.Status)
            ? stage
            : stage with
            {
                Status = SubAgentOrchestrationStageStatus.Cancelled,
                CompletedAtUtc = nowUtc,
                FailureCode = "run.cancelled",
                FailureMessage = reason.Trim()
            }).ToArray();
        var cancelled = snapshot with
        {
            Status = SubAgentOrchestrationPlanStatus.Cancelled,
            WorkItems = Array.AsReadOnly(workItems),
            Stages = Array.AsReadOnly(stages),
            FailureCode = "run.cancelled",
            FailureMessage = reason.Trim(),
            BlockingQuestions = Array.Empty<string>(),
            PauseReason = null,
            CompletedAtUtc = nowUtc
        };

        return Accept(Touch(cancelled, snapshot.Version, nowUtc));
    }

    private static SubAgentOrchestrationRunSnapshot EvaluateCurrentStage(
        SubAgentOrchestrationRunSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot.Status != SubAgentOrchestrationPlanStatus.Active || string.IsNullOrWhiteSpace(snapshot.CurrentStageId))
            return snapshot;

        var stage = snapshot.Plan.Stages.Single(item => string.Equals(item.StageId, snapshot.CurrentStageId, StringComparison.Ordinal));
        var evaluation = EvaluateGate(snapshot, stage);
        if (evaluation.State == GateEvaluationState.Waiting)
            return snapshot;
        if (evaluation.State == GateEvaluationState.Impossible)
            return FailRun(snapshot, stage, evaluation.Code!, evaluation.Message!, nowUtc);

        var stageStates = snapshot.Stages.Select(item =>
            string.Equals(item.StageId, stage.StageId, StringComparison.Ordinal)
                ? item with
                {
                    Status = SubAgentOrchestrationStageStatus.Completed,
                    CompletedAtUtc = nowUtc,
                    FailureCode = null,
                    FailureMessage = null
                }
                : item).ToArray();
        var completedStageSnapshot = snapshot with { Stages = Array.AsReadOnly(stageStates) };
        var nextStage = snapshot.Plan.Stages
            .Where(item => item.Order > stage.Order)
            .OrderBy(item => item.Order)
            .FirstOrDefault();
        if (nextStage is null)
        {
            return completedStageSnapshot with
            {
                Status = SubAgentOrchestrationPlanStatus.Completed,
                CompletedAtUtc = nowUtc,
                FailureCode = null,
                FailureMessage = null
            };
        }

        var opened = OpenStage(completedStageSnapshot with { CurrentStageId = nextStage.StageId }, nextStage, nowUtc);
        return EvaluateCurrentStage(opened, nowUtc);
    }

    private static GateEvaluation EvaluateGate(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationStage stage)
    {
        var definitions = snapshot.Plan.WorkItems
            .Where(item => string.Equals(item.StageId, stage.StageId, StringComparison.Ordinal))
            .ToDictionary(item => item.WorkItemId, StringComparer.Ordinal);
        var states = snapshot.WorkItems.Where(item => definitions.ContainsKey(item.WorkItemId)).ToArray();

        if (stage.Gate == SubAgentOrchestrationGateKind.ProposalQuorum)
            return EvaluateProposalQuorum(stage, definitions, states);
        if (stage.Gate == SubAgentOrchestrationGateKind.CritiqueCoverage)
            return EvaluateCritiqueCoverage(snapshot, stage, definitions, states);

        return EvaluateCountGate(stage, states);
    }

    private static GateEvaluation EvaluateCountGate(
        SubAgentOrchestrationStage stage,
        IReadOnlyList<SubAgentOrchestrationWorkItemState> states)
    {
        var succeeded = states.Count(item => item.Status == SubAgentOrchestrationWorkItemStatus.Succeeded);
        var possible = states.Count(item => !IsTerminal(item.Status));
        if (succeeded + possible < stage.RequiredSuccessfulItems)
            return GateEvaluation.Impossible("gate.quorum_unreachable", $"Stage '{stage.StageId}' can no longer reach {stage.RequiredSuccessfulItems} successful items.");
        if (states.Any(item => !IsTerminal(item.Status)))
            return GateEvaluation.Waiting();
        return succeeded >= stage.RequiredSuccessfulItems
            ? GateEvaluation.Satisfied()
            : GateEvaluation.Impossible("gate.quorum_not_met", $"Stage '{stage.StageId}' did not meet its success quorum.");
    }

    private static GateEvaluation EvaluateProposalQuorum(
        SubAgentOrchestrationStage stage,
        IReadOnlyDictionary<string, SubAgentOrchestrationWorkItem> definitions,
        IReadOnlyList<SubAgentOrchestrationWorkItemState> states)
    {
        var succeeded = states.Where(item => item.Status == SubAgentOrchestrationWorkItemStatus.Succeeded).ToArray();
        var possible = states.Where(item => !IsTerminal(item.Status)).ToArray();
        if (succeeded.Length + possible.Length < stage.RequiredSuccessfulItems)
            return GateEvaluation.Impossible("gate.proposal_quorum_unreachable", $"Proposal stage can no longer reach {stage.RequiredSuccessfulItems} successful proposals.");

        var possibleRoutes = succeeded.Concat(possible)
            .Select(item => definitions[item.WorkItemId].RouteKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (possibleRoutes < stage.RequiredDistinctRoutes)
            return GateEvaluation.Impossible("gate.proposal_diversity_unreachable", $"Proposal stage can no longer reach {stage.RequiredDistinctRoutes} distinct routes.");
        if (states.Any(item => !IsTerminal(item.Status)))
            return GateEvaluation.Waiting();

        var succeededRoutes = succeeded
            .Select(item => definitions[item.WorkItemId].RouteKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (succeeded.Length < stage.RequiredSuccessfulItems)
            return GateEvaluation.Impossible("gate.proposal_quorum_not_met", "Proposal success quorum was not met.");
        if (succeededRoutes < stage.RequiredDistinctRoutes)
            return GateEvaluation.Impossible("gate.proposal_diversity_not_met", "Proposal model diversity quorum was not met.");
        return GateEvaluation.Satisfied();
    }

    private static GateEvaluation EvaluateCritiqueCoverage(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationStage stage,
        IReadOnlyDictionary<string, SubAgentOrchestrationWorkItem> definitions,
        IReadOnlyList<SubAgentOrchestrationWorkItemState> states)
    {
        var proposalDefinitions = snapshot.Plan.WorkItems
            .Where(item => item.Kind == SubAgentOrchestrationStageKind.IndependentProposal)
            .ToDictionary(item => item.WorkItemId, StringComparer.Ordinal);
        var successfulProposalIds = snapshot.WorkItems
            .Where(item => proposalDefinitions.ContainsKey(item.WorkItemId))
            .Where(item => item.Status == SubAgentOrchestrationWorkItemStatus.Succeeded)
            .Select(item => item.WorkItemId)
            .ToArray();

        foreach (var proposalId in successfulProposalIds)
        {
            var critiqueStates = states
                .Where(item => string.Equals(definitions[item.WorkItemId].TargetWorkItemId, proposalId, StringComparison.Ordinal))
                .ToArray();
            var succeeded = critiqueStates.Count(item => item.Status == SubAgentOrchestrationWorkItemStatus.Succeeded);
            var possible = critiqueStates.Count(item => !IsTerminal(item.Status));
            if (succeeded + possible < stage.RequiredCritiquesPerProposal)
            {
                return GateEvaluation.Impossible(
                    "gate.critique_coverage_unreachable",
                    $"Proposal '{proposalId}' can no longer receive {stage.RequiredCritiquesPerProposal} successful critiques.");
            }
            if (critiqueStates.Any(item => !IsTerminal(item.Status)))
                return GateEvaluation.Waiting();
            if (succeeded < stage.RequiredCritiquesPerProposal)
            {
                return GateEvaluation.Impossible(
                    "gate.critique_coverage_not_met",
                    $"Proposal '{proposalId}' did not receive the required critique coverage.");
            }
        }

        return GateEvaluation.Satisfied();
    }

    private static SubAgentOrchestrationRunSnapshot OpenStage(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationStage stage,
        DateTimeOffset nowUtc)
    {
        var proposalStates = snapshot.WorkItems.ToDictionary(item => item.WorkItemId, StringComparer.Ordinal);
        var definitions = snapshot.Plan.WorkItems.ToDictionary(item => item.WorkItemId, StringComparer.Ordinal);
        var workItems = snapshot.WorkItems.Select(item =>
        {
            var definition = definitions[item.WorkItemId];
            if (!string.Equals(definition.StageId, stage.StageId, StringComparison.Ordinal))
                return item;

            if (stage.Kind == SubAgentOrchestrationStageKind.CrossCritique &&
                definition.TargetWorkItemId is not null &&
                (!proposalStates.TryGetValue(definition.TargetWorkItemId, out var target) ||
                 target.Status != SubAgentOrchestrationWorkItemStatus.Succeeded))
            {
                return item with
                {
                    Status = SubAgentOrchestrationWorkItemStatus.Skipped,
                    CompletedAtUtc = nowUtc,
                    Summary = "Skipped because the target proposal did not succeed."
                };
            }

            return item with { Status = SubAgentOrchestrationWorkItemStatus.Ready };
        }).ToArray();
        var stages = snapshot.Stages.Select(item =>
            string.Equals(item.StageId, stage.StageId, StringComparison.Ordinal)
                ? item with
                {
                    Status = SubAgentOrchestrationStageStatus.Ready,
                    OpenedAtUtc = nowUtc
                }
                : item).ToArray();

        return snapshot with
        {
            WorkItems = Array.AsReadOnly(workItems),
            Stages = Array.AsReadOnly(stages)
        };
    }

    private static SubAgentOrchestrationRunSnapshot FailRun(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationStage stage,
        string code,
        string message,
        DateTimeOffset nowUtc)
    {
        var workItems = snapshot.WorkItems.Select(item => IsTerminal(item.Status)
            ? item
            : item with
            {
                Status = SubAgentOrchestrationWorkItemStatus.Cancelled,
                CompletedAtUtc = nowUtc,
                Error = message
            }).ToArray();
        var stages = snapshot.Stages.Select(item =>
        {
            if (string.Equals(item.StageId, stage.StageId, StringComparison.Ordinal))
            {
                return item with
                {
                    Status = SubAgentOrchestrationStageStatus.Failed,
                    CompletedAtUtc = nowUtc,
                    FailureCode = code,
                    FailureMessage = message
                };
            }

            return IsTerminal(item.Status)
                ? item
                : item with
                {
                    Status = SubAgentOrchestrationStageStatus.Cancelled,
                    CompletedAtUtc = nowUtc,
                    FailureCode = "run.failed",
                    FailureMessage = message
                };
        }).ToArray();

        return snapshot with
        {
            Status = SubAgentOrchestrationPlanStatus.Failed,
            WorkItems = Array.AsReadOnly(workItems),
            Stages = Array.AsReadOnly(stages),
            FailureCode = code,
            FailureMessage = message,
            BlockingQuestions = Array.Empty<string>(),
            PauseReason = null,
            CompletedAtUtc = nowUtc
        };
    }

    private static SubAgentOrchestrationRunSnapshot Touch(
        SubAgentOrchestrationRunSnapshot snapshot,
        long previousVersion,
        DateTimeOffset nowUtc)
        => snapshot with
        {
            Version = previousVersion + 1,
            UpdatedAtUtc = nowUtc
        };

    private static SubAgentOrchestrationTransitionResult Accept(SubAgentOrchestrationRunSnapshot snapshot)
        => new()
        {
            Snapshot = snapshot,
            Issues = Array.Empty<SubAgentOrchestrationOperationIssue>()
        };

    private static SubAgentOrchestrationTransitionResult Reject(
        SubAgentOrchestrationRunSnapshot snapshot,
        string code,
        string message)
        => new()
        {
            Snapshot = snapshot,
            Issues = [new SubAgentOrchestrationOperationIssue(code, message)]
        };

    private static SubAgentOrchestrationClaimResult AcceptClaim(
        SubAgentOrchestrationRunSnapshot snapshot,
        IReadOnlyList<SubAgentOrchestrationClaimedWorkItem> claims)
        => new()
        {
            Snapshot = snapshot,
            ClaimedWorkItems = claims,
            Issues = Array.Empty<SubAgentOrchestrationOperationIssue>()
        };

    private static SubAgentOrchestrationClaimResult RejectClaim(
        SubAgentOrchestrationRunSnapshot snapshot,
        string code,
        string message)
        => new()
        {
            Snapshot = snapshot,
            ClaimedWorkItems = Array.Empty<SubAgentOrchestrationClaimedWorkItem>(),
            Issues = [new SubAgentOrchestrationOperationIssue(code, message)]
        };

    private static IReadOnlyList<string> SnapshotText(IReadOnlyList<string>? values)
        => Array.AsReadOnly((values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray());

    private static IReadOnlyList<string> MergeText(
        IReadOnlyList<string> current,
        IReadOnlyList<string> additional)
        => Array.AsReadOnly(current.Concat(additional)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray());

    private static bool HasText(IReadOnlyList<string>? values)
        => values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTerminal(SubAgentOrchestrationWorkItemStatus status)
        => status is SubAgentOrchestrationWorkItemStatus.Succeeded
            or SubAgentOrchestrationWorkItemStatus.Failed
            or SubAgentOrchestrationWorkItemStatus.Skipped
            or SubAgentOrchestrationWorkItemStatus.Cancelled;

    private static bool IsTerminal(SubAgentOrchestrationStageStatus status)
        => status is SubAgentOrchestrationStageStatus.Completed
            or SubAgentOrchestrationStageStatus.Failed
            or SubAgentOrchestrationStageStatus.Cancelled;

    private static void EnsureUniqueIds(IEnumerable<string> ids, string label, string parameterName)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException($"Orchestration {label} IDs must be non-empty and unique.", parameterName);
    }

    private enum GateEvaluationState
    {
        Waiting,
        Satisfied,
        Impossible
    }

    private sealed record GateEvaluation(
        GateEvaluationState State,
        string? Code = null,
        string? Message = null)
    {
        public static GateEvaluation Waiting() => new(GateEvaluationState.Waiting);
        public static GateEvaluation Satisfied() => new(GateEvaluationState.Satisfied);
        public static GateEvaluation Impossible(string code, string message) => new(GateEvaluationState.Impossible, code, message);
    }
}
