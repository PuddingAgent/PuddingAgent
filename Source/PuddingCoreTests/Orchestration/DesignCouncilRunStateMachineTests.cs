using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

[TestClass]
public sealed class DesignCouncilRunStateMachineTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly DesignCouncilRunStateMachine _machine = new();

    [TestMethod]
    public void CreateRun_RemainsDraftUntilExplicitActivation()
    {
        var snapshot = CreateRun();

        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Draft, snapshot.Status);
        Assert.IsNull(snapshot.CurrentStageId);
        Assert.IsTrue(snapshot.WorkItems.All(item => item.Status == SubAgentOrchestrationWorkItemStatus.Pending));

        var claim = _machine.ClaimReadyWork(snapshot, requestedCount: 1, StartedAt.AddSeconds(1));
        Assert.IsFalse(claim.Success);
        Assert.AreEqual("claim.run_not_active", claim.Issues.Single().Code);
        Assert.IsEmpty(claim.ClaimedWorkItems);
    }

    [TestMethod]
    public void Activate_OpensOnlyContextAuditStage()
    {
        var activation = _machine.Activate(CreateRun(), StartedAt.AddSeconds(1));

        Assert.IsTrue(activation.Success);
        var snapshot = activation.Snapshot;
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Active, snapshot.Status);
        Assert.AreEqual(1L, snapshot.Version);
        var currentStage = snapshot.Plan.Stages.Single(stage => stage.StageId == snapshot.CurrentStageId);
        Assert.AreEqual(SubAgentOrchestrationStageKind.ContextAudit, currentStage.Kind);

        var ready = snapshot.WorkItems.Where(item => item.Status == SubAgentOrchestrationWorkItemStatus.Ready).ToArray();
        Assert.HasCount(1, ready);
        var readyDefinition = snapshot.Plan.WorkItems.Single(item => item.WorkItemId == ready[0].WorkItemId);
        Assert.AreEqual(SubAgentOrchestrationStageKind.ContextAudit, readyDefinition.Kind);
    }

    [TestMethod]
    public void BlockingContextGap_PausesClaimsAndResumesWithUserResolution()
    {
        var snapshot = Activate(CreateRun());
        var claim = Claim(snapshot, 1);
        snapshot = claim.Snapshot;
        var work = claim.ClaimedWorkItems.Single();

        var completion = _machine.RecordCompletion(snapshot, new SubAgentWorkItemCompletion
        {
            WorkItemId = work.WorkItem.WorkItemId,
            ClaimId = work.ClaimId,
            Outcome = SubAgentWorkItemOutcome.Succeeded,
            Summary = "The requested user segment is ambiguous.",
            ContextGaps = ["No user research identifies the primary segment."],
            RequiresUserInput = true,
            BlockingQuestions = ["Which user segment is the primary target?"]
        }, StartedAt.AddMinutes(1));

        Assert.IsTrue(completion.Success);
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.AwaitingUserInput, completion.Snapshot.Status);
        CollectionAssert.Contains(completion.Snapshot.BlockingQuestions.ToArray(), "Which user segment is the primary target?");
        var pausedClaim = _machine.ClaimReadyWork(completion.Snapshot, 1, StartedAt.AddMinutes(2));
        Assert.IsFalse(pausedClaim.Success);
        Assert.AreEqual("claim.run_not_active", pausedClaim.Issues.Single().Code);

        var resume = _machine.Resume(completion.Snapshot, new SubAgentOrchestrationContextResolution
        {
            ResolutionId = "resolution-001",
            ProvidedBy = "user",
            Response = "The primary target is a solo Windows developer."
        }, StartedAt.AddMinutes(3));

        Assert.IsTrue(resume.Success);
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Active, resume.Snapshot.Status);
        Assert.HasCount(1, resume.Snapshot.ContextResolutions);
        Assert.IsEmpty(resume.Snapshot.BlockingQuestions);
        Assert.AreEqual(
            SubAgentOrchestrationStageKind.Research,
            resume.Snapshot.Plan.Stages.Single(stage => stage.StageId == resume.Snapshot.CurrentStageId).Kind);
    }

    [TestMethod]
    public void ClaimReadyWork_EnforcesPlanConcurrencyLimit()
    {
        var request = DesignCouncilPlanCompilerTests.CreateCompileRequest();
        request = request with { ExpertGroup = request.ExpertGroup with { MaxConcurrency = 2 } };
        var snapshot = Activate(CreateRun(request));
        snapshot = CompleteCurrentStage(snapshot);
        snapshot = CompleteCurrentStage(snapshot);
        AssertCurrentStage(snapshot, SubAgentOrchestrationStageKind.IndependentProposal);

        var firstClaim = Claim(snapshot, 10);
        Assert.HasCount(2, firstClaim.ClaimedWorkItems);
        var blockedByConcurrency = _machine.ClaimReadyWork(firstClaim.Snapshot, 10, StartedAt.AddMinutes(10));
        Assert.IsTrue(blockedByConcurrency.Success);
        Assert.IsEmpty(blockedByConcurrency.ClaimedWorkItems);

        var firstCompletion = Complete(firstClaim.Snapshot, firstClaim.ClaimedWorkItems[0]);
        var nextClaim = Claim(firstCompletion, 10);
        Assert.HasCount(1, nextClaim.ClaimedWorkItems);
    }

    [TestMethod]
    public void RecordCompletion_RejectsStaleOrForeignClaim()
    {
        var snapshot = Activate(CreateRun());
        var claim = Claim(snapshot, 1);
        var work = claim.ClaimedWorkItems.Single();

        var result = _machine.RecordCompletion(claim.Snapshot, new SubAgentWorkItemCompletion
        {
            WorkItemId = work.WorkItem.WorkItemId,
            ClaimId = "foreign-claim",
            Outcome = SubAgentWorkItemOutcome.Succeeded,
            Summary = "Should not be accepted."
        }, StartedAt.AddMinutes(1));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("completion.claim_mismatch", result.Issues.Single().Code);
        Assert.AreEqual(SubAgentOrchestrationWorkItemStatus.Running, result.Snapshot.WorkItems.Single(item => item.WorkItemId == work.WorkItem.WorkItemId).Status);
    }

    [TestMethod]
    public void FailedProposal_FailsRunWhenQuorumBecomesImpossible()
    {
        var snapshot = Activate(CreateRun());
        snapshot = CompleteCurrentStage(snapshot);
        snapshot = CompleteCurrentStage(snapshot);
        AssertCurrentStage(snapshot, SubAgentOrchestrationStageKind.IndependentProposal);
        var claim = Claim(snapshot, 10);
        Assert.HasCount(3, claim.ClaimedWorkItems);

        snapshot = Complete(claim.Snapshot, claim.ClaimedWorkItems[0]);
        snapshot = Complete(snapshot, claim.ClaimedWorkItems[1]);
        var failed = Fail(snapshot, claim.ClaimedWorkItems[2], "Provider unavailable.");

        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Failed, failed.Status);
        Assert.AreEqual("gate.proposal_quorum_unreachable", failed.FailureCode);
        Assert.IsNotNull(failed.CompletedAtUtc);
        Assert.IsTrue(failed.Stages.Any(stage => stage.Status == SubAgentOrchestrationStageStatus.Failed));
    }

    [TestMethod]
    public void ExtraProposalFailure_PreservesQuorumAndSkipsItsCritiques()
    {
        var request = DesignCouncilPlanCompilerTests.CreateCompileRequest();
        var members = request.ExpertGroup.Members.Append(new ExpertGroupMemberDefinition
        {
            MemberId = "security",
            Role = "security-architect",
            TemplateId = "security-architect",
            RouteKey = "opencode/security-model",
            Capabilities = ExpertMemberCapabilities.Propose | ExpertMemberCapabilities.Critique
        }).ToArray();
        request = request with { ExpertGroup = request.ExpertGroup with { Members = members } };
        var snapshot = Activate(CreateRun(request));
        snapshot = CompleteCurrentStage(snapshot);
        snapshot = CompleteCurrentStage(snapshot);
        var claim = Claim(snapshot, 10);
        Assert.HasCount(4, claim.ClaimedWorkItems);

        var failedProposal = claim.ClaimedWorkItems.Single(item => item.WorkItem.MemberId == "security");
        snapshot = claim.ClaimedWorkItems
            .Where(item => item.WorkItem.MemberId != "security")
            .Aggregate(claim.Snapshot, (current, item) => Complete(current, item));
        snapshot = Fail(snapshot, failedProposal, "Security specialist unavailable.");

        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Active, snapshot.Status);
        AssertCurrentStage(snapshot, SubAgentOrchestrationStageKind.CrossCritique);
        var skipped = snapshot.WorkItems
            .Where(item => item.Status == SubAgentOrchestrationWorkItemStatus.Skipped)
            .Select(item => snapshot.Plan.WorkItems.Single(definition => definition.WorkItemId == item.WorkItemId))
            .ToArray();
        Assert.IsNotEmpty(skipped);
        Assert.IsTrue(skipped.All(item => item.TargetWorkItemId == failedProposal.WorkItem.WorkItemId));
    }

    [TestMethod]
    public void SuccessfulRun_TraversesAllGatesToCompletion()
    {
        var snapshot = Activate(CreateRun());

        while (snapshot.Status == SubAgentOrchestrationPlanStatus.Active)
            snapshot = CompleteCurrentStage(snapshot);

        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Completed, snapshot.Status);
        Assert.IsNotNull(snapshot.CompletedAtUtc);
        Assert.IsTrue(snapshot.Stages.All(stage => stage.Status == SubAgentOrchestrationStageStatus.Completed));
        Assert.IsTrue(snapshot.WorkItems.All(item => item.Status == SubAgentOrchestrationWorkItemStatus.Succeeded));
    }

    [TestMethod]
    public void Cancel_MarksAllOutstandingWorkTerminal()
    {
        var snapshot = Activate(CreateRun());
        var claim = Claim(snapshot, 1);

        var cancellation = _machine.Cancel(claim.Snapshot, "User cancelled the design council.", StartedAt.AddMinutes(1));

        Assert.IsTrue(cancellation.Success);
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Cancelled, cancellation.Snapshot.Status);
        Assert.AreEqual("run.cancelled", cancellation.Snapshot.FailureCode);
        Assert.IsTrue(cancellation.Snapshot.WorkItems.All(item => item.Status == SubAgentOrchestrationWorkItemStatus.Cancelled));
    }

    private SubAgentOrchestrationRunSnapshot CreateRun(DesignCouncilPlanCompileRequest? request = null)
    {
        var compilation = new DesignCouncilPlanCompiler().Compile(request ?? DesignCouncilPlanCompilerTests.CreateCompileRequest());
        Assert.IsTrue(compilation.Success, string.Join(Environment.NewLine, compilation.Issues.Select(issue => issue.Message)));
        return _machine.CreateRun(compilation.Plan!, "moa-run-001", StartedAt);
    }

    private SubAgentOrchestrationRunSnapshot Activate(SubAgentOrchestrationRunSnapshot snapshot)
    {
        var result = _machine.Activate(snapshot, snapshot.UpdatedAtUtc.AddSeconds(1));
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        return result.Snapshot;
    }

    private SubAgentOrchestrationClaimResult Claim(SubAgentOrchestrationRunSnapshot snapshot, int count)
    {
        var result = _machine.ClaimReadyWork(snapshot, count, snapshot.UpdatedAtUtc.AddSeconds(1));
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        return result;
    }

    private SubAgentOrchestrationRunSnapshot CompleteCurrentStage(SubAgentOrchestrationRunSnapshot snapshot)
    {
        var stageId = snapshot.CurrentStageId;
        while (snapshot.Status == SubAgentOrchestrationPlanStatus.Active && snapshot.CurrentStageId == stageId)
        {
            var claim = Claim(snapshot, 100);
            Assert.IsNotEmpty(claim.ClaimedWorkItems, "An active stage must expose work or transition terminal.");
            snapshot = claim.Snapshot;
            foreach (var item in claim.ClaimedWorkItems)
                snapshot = Complete(snapshot, item);
        }

        return snapshot;
    }

    private SubAgentOrchestrationRunSnapshot Complete(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationClaimedWorkItem item)
    {
        var result = _machine.RecordCompletion(snapshot, new SubAgentWorkItemCompletion
        {
            WorkItemId = item.WorkItem.WorkItemId,
            ClaimId = item.ClaimId,
            Outcome = SubAgentWorkItemOutcome.Succeeded,
            Summary = $"Completed by {item.WorkItem.MemberId}.",
            OutputReference = $"artifact://{item.WorkItem.WorkItemId}"
        }, snapshot.UpdatedAtUtc.AddSeconds(1));
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        return result.Snapshot;
    }

    private SubAgentOrchestrationRunSnapshot Fail(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationClaimedWorkItem item,
        string error)
    {
        var result = _machine.RecordCompletion(snapshot, new SubAgentWorkItemCompletion
        {
            WorkItemId = item.WorkItem.WorkItemId,
            ClaimId = item.ClaimId,
            Outcome = SubAgentWorkItemOutcome.Failed,
            Error = error
        }, snapshot.UpdatedAtUtc.AddSeconds(1));
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        return result.Snapshot;
    }

    private static void AssertCurrentStage(
        SubAgentOrchestrationRunSnapshot snapshot,
        SubAgentOrchestrationStageKind expected)
        => Assert.AreEqual(expected, snapshot.Plan.Stages.Single(stage => stage.StageId == snapshot.CurrentStageId).Kind);
}
