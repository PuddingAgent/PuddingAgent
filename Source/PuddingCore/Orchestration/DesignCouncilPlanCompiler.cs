namespace PuddingCode.Orchestration;

/// <summary>
/// Compiles a design request and an expert-group definition into a deterministic, inspectable DAG.
/// This type is intentionally pure: it performs no I/O and cannot start a sub-agent.
/// </summary>
public sealed class DesignCouncilPlanCompiler
{
    private const int DoubleReviewCount = 2;

    public DesignCouncilPlanCompilationResult Compile(DesignCouncilPlanCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = Validate(request);
        if (issues.Count > 0)
        {
            return new DesignCouncilPlanCompilationResult
            {
                Issues = issues
            };
        }

        var group = request.ExpertGroup;
        var members = group.Members.ToArray();
        var chair = members.Single(member => IdEquals(member.MemberId, group.ChairMemberId));
        var auditors = WithCapability(members, ExpertMemberCapabilities.ContextAudit);
        var researchers = WithCapability(members, ExpertMemberCapabilities.Research);
        var proposers = WithCapability(members, ExpertMemberCapabilities.Propose);
        var critics = WithCapability(members, ExpertMemberCapabilities.Critique);
        var finalReviewer = members.First(member =>
            HasCapability(member, ExpertMemberCapabilities.FinalReview) &&
            !IdEquals(member.MemberId, chair.MemberId) &&
            !HasCapability(member, ExpertMemberCapabilities.Propose) &&
            !RouteEquals(member.RouteKey, chair.RouteKey));

        var contextStage = CreateStage(
            request.PlanId,
            SubAgentOrchestrationStageKind.ContextAudit,
            SubAgentOrchestrationGateKind.ContextResolved,
            order: 0,
            requiredSuccessfulItems: auditors.Count,
            pauseOnCriticalContextGap: true);
        var researchStage = CreateStage(
            request.PlanId,
            SubAgentOrchestrationStageKind.Research,
            SubAgentOrchestrationGateKind.EvidenceAvailable,
            order: 1,
            requiredSuccessfulItems: 1,
            dependencies: [contextStage.StageId]);
        var proposalStage = CreateStage(
            request.PlanId,
            SubAgentOrchestrationStageKind.IndependentProposal,
            SubAgentOrchestrationGateKind.ProposalQuorum,
            order: 2,
            requiredSuccessfulItems: group.MinimumSuccessfulProposals,
            requiredDistinctRoutes: group.MinimumDistinctProposalRoutes,
            dependencies: [researchStage.StageId]);

        var workItems = new List<SubAgentOrchestrationWorkItem>();
        AddMemberWorkItems(
            workItems,
            request.PlanId,
            contextStage,
            auditors,
            SubAgentWorkItemContextVisibility.CanonicalRequestOnly,
            "Verify the stated user intent against supplied evidence. Identify assumptions, missing user research, and critical context gaps. Do not propose a solution.",
            "intentAssessment, evidenceAssessment, contextGaps, criticalQuestions, canProceed");
        AddMemberWorkItems(
            workItems,
            request.PlanId,
            researchStage,
            researchers,
            SubAgentWorkItemContextVisibility.CanonicalRequestOnly,
            "Research comparable products, open-source implementations, constraints, and failure cases relevant to the canonical request. Separate facts from inference.",
            "findings, sources, comparableCases, constraints, unresolvedQuestions");
        AddMemberWorkItems(
            workItems,
            request.PlanId,
            proposalStage,
            proposers,
            SubAgentWorkItemContextVisibility.CanonicalRequestAndResearch,
            "Produce an independent design from your assigned specialty. Do not inspect or imitate another member's proposal.",
            "proposal, assumptions, alternatives, tradeoffs, risks, validationPlan");

        var proposalItems = workItems
            .Where(item => item.Kind == SubAgentOrchestrationStageKind.IndependentProposal)
            .ToArray();
        var critiqueAssignments = BuildCritiqueAssignments(group, chair, critics, proposalItems);
        var critiqueStage = CreateStage(
            request.PlanId,
            SubAgentOrchestrationStageKind.CrossCritique,
            SubAgentOrchestrationGateKind.CritiqueCoverage,
            order: 3,
            requiredSuccessfulItems: critiqueAssignments.Count,
            requiredCritiquesPerProposal: group.CritiqueTopology == CritiqueTopology.DoubleReview
                ? DoubleReviewCount
                : critiqueAssignments.GroupBy(item => item.Proposal.WorkItemId).Min(grouping => grouping.Count()),
            dependencies: [proposalStage.StageId]);

        for (var index = 0; index < critiqueAssignments.Count; index++)
        {
            var assignment = critiqueAssignments[index];
            workItems.Add(CreateWorkItem(
                request.PlanId,
                critiqueStage,
                index,
                assignment.Critic,
                SubAgentWorkItemContextVisibility.TargetProposalAndEvidence,
                "Critique the assigned proposal for user-intent fit, evidence, feasibility, cost, reversibility, observability, missing context, and counterexamples. Do not rewrite it as your own proposal.",
                "targetProposalId, acceptedClaims, rejectedClaims, risks, missingContext, recommendedChanges",
                assignment.Proposal.WorkItemId));
        }

        var synthesisStage = CreateStage(
            request.PlanId,
            SubAgentOrchestrationStageKind.Synthesis,
            SubAgentOrchestrationGateKind.ChairSynthesis,
            order: 4,
            requiredSuccessfulItems: 1,
            dependencies: [critiqueStage.StageId]);
        workItems.Add(CreateWorkItem(
            request.PlanId,
            synthesisStage,
            0,
            chair,
            SubAgentWorkItemContextVisibility.AllPriorOutputs,
            "Synthesize the evidence, independent proposals, and critiques. Do not use majority vote. Record accepted and rejected ideas with reasons, unresolved disagreements, assumptions, and any user questions that still block a sound decision.",
            "recommendedDesign, decisionLog, rejectedAlternatives, unresolvedDisagreements, blockingQuestions, implementationStages, acceptancePlan"));

        var finalReviewStage = CreateStage(
            request.PlanId,
            SubAgentOrchestrationStageKind.FinalReview,
            SubAgentOrchestrationGateKind.IndependentFinalReview,
            order: 5,
            requiredSuccessfulItems: 1,
            dependencies: [synthesisStage.StageId]);
        workItems.Add(CreateWorkItem(
            request.PlanId,
            finalReviewStage,
            0,
            finalReviewer,
            SubAgentWorkItemContextVisibility.AllPriorOutputs,
            "Perform an adversarial final review of the synthesized design. Verify that it follows the canonical request, preserves evidence provenance, addresses critiques, and does not conceal unresolved context gaps.",
            "verdict, blockingFindings, nonBlockingFindings, evidenceTrace, requiredRevisions"));

        return new DesignCouncilPlanCompilationResult
        {
            Issues = Array.Empty<SubAgentOrchestrationValidationIssue>(),
            Plan = new SubAgentOrchestrationPlan
            {
                PlanId = request.PlanId.Trim(),
                DesignRequest = SnapshotDesignRequest(request.DesignRequest),
                ExpertGroupId = group.GroupId.Trim(),
                Status = SubAgentOrchestrationPlanStatus.Draft,
                RequiresExplicitActivation = true,
                AllowFallback = false,
                MaxConcurrency = group.MaxConcurrency,
                Stages = Array.AsReadOnly(new[]
                {
                    contextStage,
                    researchStage,
                    proposalStage,
                    critiqueStage,
                    synthesisStage,
                    finalReviewStage
                }),
                WorkItems = workItems.AsReadOnly()
            }
        };
    }

    private static List<SubAgentOrchestrationValidationIssue> Validate(DesignCouncilPlanCompileRequest request)
    {
        var issues = new List<SubAgentOrchestrationValidationIssue>();
        RequireText(request.PlanId, "plan.id_required", "PlanId is required.", "planId", issues);

        var design = request.DesignRequest;
        if (design is null)
        {
            issues.Add(new("request.required", "DesignRequest is required.", "designRequest"));
            return issues;
        }

        RequireText(design.RequestId, "request.id_required", "RequestId is required.", "designRequest.requestId", issues);
        RequireText(design.WorkspaceId, "request.workspace_required", "WorkspaceId is required.", "designRequest.workspaceId", issues);
        RequireText(design.ParentSessionId, "request.session_required", "ParentSessionId is required.", "designRequest.parentSessionId", issues);
        RequireText(design.RequestedByAgentId, "request.agent_required", "RequestedByAgentId is required.", "designRequest.requestedByAgentId", issues);
        RequireText(design.UserIntent, "request.intent_required", "UserIntent is required.", "designRequest.userIntent", issues);
        RequireText(design.ProblemStatement, "request.problem_required", "ProblemStatement is required.", "designRequest.problemStatement", issues);
        if (!HasText(design.AcceptanceCriteria))
            issues.Add(new("request.acceptance_criteria_required", "At least one acceptance criterion is required.", "designRequest.acceptanceCriteria"));
        if (!HasText(design.RequestedDeliverables))
            issues.Add(new("request.deliverable_required", "At least one requested deliverable is required.", "designRequest.requestedDeliverables"));

        var group = request.ExpertGroup;
        if (group is null)
        {
            issues.Add(new("group.required", "ExpertGroup is required.", "expertGroup"));
            return issues;
        }

        RequireText(group.GroupId, "group.id_required", "GroupId is required.", "expertGroup.groupId", issues);
        RequireText(group.ChairMemberId, "group.chair_required", "ChairMemberId is required.", "expertGroup.chairMemberId", issues);
        if (group.AllowFallback)
            issues.Add(new("group.fallback_not_allowed", "MOA expert groups require exact model routes; fallback must be disabled.", "expertGroup.allowFallback"));
        if (group.MinimumMembers < 3)
            issues.Add(new("group.minimum_members_invalid", "MinimumMembers must be at least 3.", "expertGroup.minimumMembers"));
        if (group.MinimumSuccessfulProposals < 2)
            issues.Add(new("group.proposal_quorum_invalid", "MinimumSuccessfulProposals must be at least 2.", "expertGroup.minimumSuccessfulProposals"));
        if (group.MinimumDistinctProposalRoutes < 2)
            issues.Add(new("group.model_diversity_invalid", "MinimumDistinctProposalRoutes must be at least 2.", "expertGroup.minimumDistinctProposalRoutes"));
        if (group.MaxConcurrency < 1)
            issues.Add(new("group.max_concurrency_invalid", "MaxConcurrency must be positive.", "expertGroup.maxConcurrency"));
        if (group.Members.Count < group.MinimumMembers)
            issues.Add(new("group.members_below_minimum", $"Expert group has {group.Members.Count} members but requires {group.MinimumMembers}.", "expertGroup.members"));

        var duplicateMemberIds = group.Members
            .Where(member => !string.IsNullOrWhiteSpace(member.MemberId))
            .GroupBy(member => member.MemberId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(grouping => grouping.Count() > 1)
            .Select(grouping => grouping.Key)
            .ToArray();
        foreach (var duplicateMemberId in duplicateMemberIds)
            issues.Add(new("group.member_id_duplicate", $"MemberId '{duplicateMemberId}' is duplicated.", "expertGroup.members"));

        for (var index = 0; index < group.Members.Count; index++)
        {
            var member = group.Members[index];
            var path = $"expertGroup.members[{index}]";
            RequireText(member.MemberId, "group.member_id_required", "MemberId is required.", $"{path}.memberId", issues);
            RequireText(member.Role, "group.member_role_required", "Role is required.", $"{path}.role", issues);
            RequireText(member.TemplateId, "group.member_template_required", "TemplateId is required.", $"{path}.templateId", issues);
            RequireText(member.RouteKey, "group.member_route_required", "An exact provider/model RouteKey is required.", $"{path}.routeKey", issues);
            if (member.Capabilities == ExpertMemberCapabilities.None)
                issues.Add(new("group.member_capability_required", "At least one capability is required.", $"{path}.capabilities"));
        }

        if (issues.Any(issue => issue.Code is "group.member_id_required" or "group.member_route_required"))
            return issues;

        var members = group.Members.ToArray();
        var chair = members.FirstOrDefault(member => IdEquals(member.MemberId, group.ChairMemberId));
        if (chair is null)
        {
            issues.Add(new("group.chair_not_found", "ChairMemberId must reference a configured member.", "expertGroup.chairMemberId"));
        }
        else if (!HasCapability(chair, ExpertMemberCapabilities.Synthesize))
        {
            issues.Add(new("group.chair_cannot_synthesize", "The chair must have the Synthesize capability.", "expertGroup.chairMemberId"));
        }

        var auditors = WithCapability(members, ExpertMemberCapabilities.ContextAudit);
        var researchers = WithCapability(members, ExpertMemberCapabilities.Research);
        var proposers = WithCapability(members, ExpertMemberCapabilities.Propose);
        var critics = WithCapability(members, ExpertMemberCapabilities.Critique);
        if (auditors.Count == 0)
            issues.Add(new("group.context_auditor_missing", "At least one context auditor is required.", "expertGroup.members"));
        if (researchers.Count == 0)
            issues.Add(new("group.researcher_missing", "At least one researcher is required.", "expertGroup.members"));
        if (proposers.Count < group.MinimumSuccessfulProposals)
            issues.Add(new("group.proposal_quorum_unavailable", $"Proposal quorum requires {group.MinimumSuccessfulProposals} proposers but only {proposers.Count} are configured.", "expertGroup.members"));

        var distinctProposalRoutes = proposers
            .Select(member => member.RouteKey.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctProposalRoutes < group.MinimumDistinctProposalRoutes)
            issues.Add(new("group.model_diversity_insufficient", $"Proposal members provide {distinctProposalRoutes} distinct routes but {group.MinimumDistinctProposalRoutes} are required.", "expertGroup.members"));

        var finalReviewer = chair is null
            ? null
            : members.FirstOrDefault(member =>
                HasCapability(member, ExpertMemberCapabilities.FinalReview) &&
                !IdEquals(member.MemberId, chair.MemberId) &&
                !HasCapability(member, ExpertMemberCapabilities.Propose) &&
                !RouteEquals(member.RouteKey, chair.RouteKey));
        if (finalReviewer is null)
            issues.Add(new("group.independent_final_reviewer_missing", "Final review requires a non-chair, non-proposer member on a route distinct from the chair.", "expertGroup.members"));

        if (chair is not null)
        {
            var critiquesRequired = group.CritiqueTopology == CritiqueTopology.DoubleReview
                ? DoubleReviewCount
                : 1;
            foreach (var proposer in proposers)
            {
                var eligibleCritics = critics.Count(critic => IsEligibleCritic(critic, proposer, chair));
                if (eligibleCritics < critiquesRequired)
                {
                    issues.Add(new(
                        "group.critique_coverage_insufficient",
                        $"Proposal member '{proposer.MemberId}' has {eligibleCritics} eligible critics but requires {critiquesRequired}.",
                        "expertGroup.members"));
                }
            }
        }

        return issues;
    }

    private static List<CritiqueAssignment> BuildCritiqueAssignments(
        ExpertGroupDefinition group,
        ExpertGroupMemberDefinition chair,
        IReadOnlyList<ExpertGroupMemberDefinition> critics,
        IReadOnlyList<SubAgentOrchestrationWorkItem> proposals)
    {
        var assignments = new List<CritiqueAssignment>();
        for (var proposalIndex = 0; proposalIndex < proposals.Count; proposalIndex++)
        {
            var proposal = proposals[proposalIndex];
            var proposer = group.Members.Single(member => IdEquals(member.MemberId, proposal.MemberId));
            var eligible = critics
                .Where(critic => IsEligibleCritic(critic, proposer, chair))
                .ToArray();

            if (group.CritiqueTopology == CritiqueTopology.DoubleReview)
            {
                var rotation = proposalIndex % eligible.Length;
                eligible = eligible.Skip(rotation).Concat(eligible.Take(rotation)).Take(DoubleReviewCount).ToArray();
            }

            assignments.AddRange(eligible.Select(critic => new CritiqueAssignment(proposal, critic)));
        }

        return assignments;
    }

    private static bool IsEligibleCritic(
        ExpertGroupMemberDefinition critic,
        ExpertGroupMemberDefinition proposer,
        ExpertGroupMemberDefinition chair)
        => !IdEquals(critic.MemberId, proposer.MemberId)
           && !IdEquals(critic.MemberId, chair.MemberId)
           && !RouteEquals(critic.RouteKey, proposer.RouteKey);

    private static SubAgentOrchestrationStage CreateStage(
        string planId,
        SubAgentOrchestrationStageKind kind,
        SubAgentOrchestrationGateKind gate,
        int order,
        int requiredSuccessfulItems,
        int requiredDistinctRoutes = 0,
        int requiredCritiquesPerProposal = 0,
        bool pauseOnCriticalContextGap = false,
        IReadOnlyList<string>? dependencies = null)
        => new()
        {
            StageId = $"{planId.Trim()}/stage/{ToToken(kind)}",
            Kind = kind,
            Gate = gate,
            Order = order,
            RequiredSuccessfulItems = requiredSuccessfulItems,
            RequiredDistinctRoutes = requiredDistinctRoutes,
            RequiredCritiquesPerProposal = requiredCritiquesPerProposal,
            PauseOnCriticalContextGap = pauseOnCriticalContextGap,
            DependsOnStageIds = dependencies is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(dependencies.ToArray())
        };

    private static void AddMemberWorkItems(
        ICollection<SubAgentOrchestrationWorkItem> destination,
        string planId,
        SubAgentOrchestrationStage stage,
        IReadOnlyList<ExpertGroupMemberDefinition> members,
        SubAgentWorkItemContextVisibility visibility,
        string instruction,
        string expectedOutputContract)
    {
        for (var index = 0; index < members.Count; index++)
        {
            destination.Add(CreateWorkItem(
                planId,
                stage,
                index,
                members[index],
                visibility,
                instruction,
                expectedOutputContract));
        }
    }

    private static SubAgentOrchestrationWorkItem CreateWorkItem(
        string planId,
        SubAgentOrchestrationStage stage,
        int index,
        ExpertGroupMemberDefinition member,
        SubAgentWorkItemContextVisibility visibility,
        string instruction,
        string expectedOutputContract,
        string? targetWorkItemId = null)
        => new()
        {
            WorkItemId = $"{planId.Trim()}/work/{ToToken(stage.Kind)}/{index + 1:D3}",
            StageId = stage.StageId,
            Kind = stage.Kind,
            MemberId = member.MemberId.Trim(),
            Role = member.Role.Trim(),
            TemplateId = member.TemplateId.Trim(),
            RouteKey = member.RouteKey.Trim(),
            ContextVisibility = visibility,
            Instruction = instruction,
            ExpectedOutputContract = expectedOutputContract,
            TargetWorkItemId = targetWorkItemId,
            IsReadOnly = true
        };

    private static IReadOnlyList<ExpertGroupMemberDefinition> WithCapability(
        IReadOnlyList<ExpertGroupMemberDefinition> members,
        ExpertMemberCapabilities capability)
        => members.Where(member => HasCapability(member, capability)).ToArray();

    private static bool HasCapability(ExpertGroupMemberDefinition member, ExpertMemberCapabilities capability)
        => (member.Capabilities & capability) == capability;

    private static DesignRequest SnapshotDesignRequest(DesignRequest request)
        => request with
        {
            RequestId = request.RequestId.Trim(),
            WorkspaceId = request.WorkspaceId.Trim(),
            ParentSessionId = request.ParentSessionId.Trim(),
            RequestedByAgentId = request.RequestedByAgentId.Trim(),
            UserIntent = request.UserIntent.Trim(),
            ProblemStatement = request.ProblemStatement.Trim(),
            IntentEvidence = SnapshotText(request.IntentEvidence),
            Constraints = SnapshotText(request.Constraints),
            NonGoals = SnapshotText(request.NonGoals),
            KnownContext = SnapshotText(request.KnownContext),
            SuspectedContextGaps = SnapshotText(request.SuspectedContextGaps),
            ResearchQuestions = SnapshotText(request.ResearchQuestions),
            AcceptanceCriteria = SnapshotText(request.AcceptanceCriteria),
            RequestedDeliverables = SnapshotText(request.RequestedDeliverables)
        };

    private static IReadOnlyList<string> SnapshotText(IReadOnlyList<string>? values)
        => Array.AsReadOnly((values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray());

    private static bool HasText(IReadOnlyList<string>? values)
        => values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

    private static void RequireText(
        string? value,
        string code,
        string message,
        string path,
        ICollection<SubAgentOrchestrationValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new(code, message, path));
    }

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool RouteEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string ToToken(SubAgentOrchestrationStageKind kind)
        => kind switch
        {
            SubAgentOrchestrationStageKind.ContextAudit => "context-audit",
            SubAgentOrchestrationStageKind.Research => "research",
            SubAgentOrchestrationStageKind.IndependentProposal => "independent-proposal",
            SubAgentOrchestrationStageKind.CrossCritique => "cross-critique",
            SubAgentOrchestrationStageKind.Synthesis => "synthesis",
            SubAgentOrchestrationStageKind.FinalReview => "final-review",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private sealed record CritiqueAssignment(
        SubAgentOrchestrationWorkItem Proposal,
        ExpertGroupMemberDefinition Critic);
}
