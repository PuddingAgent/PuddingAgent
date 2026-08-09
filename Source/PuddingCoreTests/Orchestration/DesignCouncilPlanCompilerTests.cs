using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

[TestClass]
public sealed class DesignCouncilPlanCompilerTests
{
    private readonly DesignCouncilPlanCompiler _compiler = new();

    [TestMethod]
    public void Compile_ProducesDraftDagThatRequiresExplicitActivation()
    {
        var result = _compiler.Compile(CreateCompileRequest());

        Assert.IsTrue(result.Success, FormatIssues(result));
        Assert.IsNotNull(result.Plan);
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Draft, result.Plan!.Status);
        Assert.IsTrue(result.Plan.RequiresExplicitActivation);
        Assert.IsFalse(result.Plan.AllowFallback);
        CollectionAssert.AreEqual(
            new[]
            {
                SubAgentOrchestrationStageKind.ContextAudit,
                SubAgentOrchestrationStageKind.Research,
                SubAgentOrchestrationStageKind.IndependentProposal,
                SubAgentOrchestrationStageKind.CrossCritique,
                SubAgentOrchestrationStageKind.Synthesis,
                SubAgentOrchestrationStageKind.FinalReview
            },
            result.Plan.Stages.OrderBy(stage => stage.Order).Select(stage => stage.Kind).ToArray());

        var contextGate = result.Plan.Stages.Single(stage => stage.Kind == SubAgentOrchestrationStageKind.ContextAudit);
        Assert.AreEqual(SubAgentOrchestrationGateKind.ContextResolved, contextGate.Gate);
        Assert.IsTrue(contextGate.PauseOnCriticalContextGap);
        Assert.IsEmpty(contextGate.DependsOnStageIds);
    }

    [TestMethod]
    public void Compile_KeepsProposalsIndependentAndModelDiverse()
    {
        var result = _compiler.Compile(CreateCompileRequest());
        Assert.IsTrue(result.Success, FormatIssues(result));

        var plan = result.Plan!;
        var proposalStage = plan.Stages.Single(stage => stage.Kind == SubAgentOrchestrationStageKind.IndependentProposal);
        var proposals = plan.WorkItems.Where(item => item.Kind == SubAgentOrchestrationStageKind.IndependentProposal).ToArray();

        Assert.HasCount(3, proposals);
        Assert.AreEqual(3, proposals.Select(item => item.RouteKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.AreEqual(3, proposalStage.RequiredSuccessfulItems);
        Assert.AreEqual(3, proposalStage.RequiredDistinctRoutes);
        Assert.IsTrue(proposals.All(item => item.ContextVisibility == SubAgentWorkItemContextVisibility.CanonicalRequestAndResearch));
        Assert.IsTrue(proposals.All(item => item.TargetWorkItemId is null));
        Assert.IsTrue(proposals.All(item => item.IsReadOnly));
    }

    [TestMethod]
    public void Compile_SnapshotsCanonicalRequestBeforeAnyExecutionCanBegin()
    {
        var knownContext = new List<string> { "original context" };
        var request = CreateCompileRequest();
        request = request with
        {
            DesignRequest = request.DesignRequest with { KnownContext = knownContext }
        };

        var result = _compiler.Compile(request);
        Assert.IsTrue(result.Success, FormatIssues(result));

        knownContext[0] = "mutated after compilation";

        Assert.AreEqual("original context", result.Plan!.DesignRequest.KnownContext.Single());
    }

    [TestMethod]
    public void Compile_AssignsTwoIndependentCriticsPerProposalWithoutSelfReview()
    {
        var result = _compiler.Compile(CreateCompileRequest());
        Assert.IsTrue(result.Success, FormatIssues(result));

        var plan = result.Plan!;
        var proposals = plan.WorkItems.Where(item => item.Kind == SubAgentOrchestrationStageKind.IndependentProposal).ToDictionary(item => item.WorkItemId);
        var critiques = plan.WorkItems.Where(item => item.Kind == SubAgentOrchestrationStageKind.CrossCritique).ToArray();
        var critiqueStage = plan.Stages.Single(stage => stage.Kind == SubAgentOrchestrationStageKind.CrossCritique);

        Assert.AreEqual(2, critiqueStage.RequiredCritiquesPerProposal);
        Assert.HasCount(proposals.Count * 2, critiques);
        foreach (var grouping in critiques.GroupBy(item => item.TargetWorkItemId))
        {
            Assert.IsNotNull(grouping.Key);
            Assert.AreEqual(2, grouping.Count());
            var proposal = proposals[grouping.Key!];
            Assert.IsTrue(grouping.All(critique => !string.Equals(critique.MemberId, proposal.MemberId, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(grouping.All(critique => !string.Equals(critique.RouteKey, proposal.RouteKey, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(grouping.All(critique => critique.MemberId != "architect"), "The synthesis chair must not pre-review proposals.");
        }
    }

    [TestMethod]
    public void Compile_AllToAllAssignsEveryEligibleNonChairCritic()
    {
        var request = CreateCompileRequest();
        request = request with
        {
            ExpertGroup = request.ExpertGroup with { CritiqueTopology = CritiqueTopology.AllToAll }
        };

        var result = _compiler.Compile(request);
        Assert.IsTrue(result.Success, FormatIssues(result));

        var plan = result.Plan!;
        var critiques = plan.WorkItems.Where(item => item.Kind == SubAgentOrchestrationStageKind.CrossCritique).ToArray();
        var critiqueStage = plan.Stages.Single(stage => stage.Kind == SubAgentOrchestrationStageKind.CrossCritique);

        Assert.HasCount(10, critiques);
        Assert.AreEqual(3, critiqueStage.RequiredCritiquesPerProposal);
        Assert.IsTrue(critiques.All(item => item.MemberId != "architect"));
    }

    [TestMethod]
    public void Compile_RejectsFallbackBecauseItInvalidatesDiversityClaims()
    {
        var request = CreateCompileRequest();
        request = request with { ExpertGroup = request.ExpertGroup with { AllowFallback = true } };

        var result = _compiler.Compile(request);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Plan);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "group.fallback_not_allowed"));
    }

    [TestMethod]
    public void Compile_RejectsProposalPoolWithoutDistinctModelRoutes()
    {
        var request = CreateCompileRequest();
        var members = request.ExpertGroup.Members
            .Select(member => member.Capabilities.HasFlag(ExpertMemberCapabilities.Propose)
                ? member with { RouteKey = "deepseek/shared-model" }
                : member)
            .ToArray();
        request = request with { ExpertGroup = request.ExpertGroup with { Members = members } };

        var result = _compiler.Compile(request);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Plan);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "group.model_diversity_insufficient"));
    }

    [TestMethod]
    public void Compile_RequiresFinalReviewerIndependentFromChairAndProposalAuthors()
    {
        var request = CreateCompileRequest();
        var members = request.ExpertGroup.Members
            .Select(member => member.MemberId == "reviewer"
                ? member with { Capabilities = member.Capabilities | ExpertMemberCapabilities.Propose }
                : member)
            .ToArray();
        request = request with { ExpertGroup = request.ExpertGroup with { Members = members } };

        var result = _compiler.Compile(request);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "group.independent_final_reviewer_missing"));
    }

    private static DesignCouncilPlanCompileRequest CreateCompileRequest()
        => new()
        {
            PlanId = "moa-design-001",
            DesignRequest = new DesignRequest
            {
                RequestId = "design-001",
                WorkspaceId = "default",
                ParentSessionId = "session-001",
                RequestedByAgentId = "main-agent",
                UserIntent = "Design a durable MOA orchestration core before implementing execution.",
                ProblemStatement = "Multiple specialist models must produce independent designs, critique them, and synthesize an auditable decision.",
                IntentEvidence = ["The user explicitly requested planning before execution."],
                KnownContext = ["Pudding already supports isolated sub-agent runs."],
                ResearchQuestions = ["Which comparable multi-agent review topologies are effective?"],
                AcceptanceCriteria = ["Compilation never dispatches child agents."],
                RequestedDeliverables = ["An inspectable orchestration DAG."]
            },
            ExpertGroup = new ExpertGroupDefinition
            {
                GroupId = "design-council",
                ChairMemberId = "architect",
                MinimumMembers = 5,
                MinimumSuccessfulProposals = 3,
                MinimumDistinctProposalRoutes = 3,
                MaxConcurrency = 4,
                CritiqueTopology = CritiqueTopology.DoubleReview,
                AllowFallback = false,
                Members =
                [
                    new ExpertGroupMemberDefinition
                    {
                        MemberId = "context-research",
                        Role = "intent-and-market-researcher",
                        TemplateId = "researcher",
                        RouteKey = "deepseek/deepseek-v4-flash",
                        Capabilities = ExpertMemberCapabilities.ContextAudit |
                                       ExpertMemberCapabilities.Research |
                                       ExpertMemberCapabilities.Critique
                    },
                    new ExpertGroupMemberDefinition
                    {
                        MemberId = "architect",
                        Role = "architecture-chair",
                        TemplateId = "architect",
                        RouteKey = "deepseek/deepseek-v4-pro",
                        Capabilities = ExpertMemberCapabilities.Propose |
                                       ExpertMemberCapabilities.Critique |
                                       ExpertMemberCapabilities.Synthesize
                    },
                    new ExpertGroupMemberDefinition
                    {
                        MemberId = "frontend",
                        Role = "frontend-developer",
                        TemplateId = "frontend-developer",
                        RouteKey = "opencode/kimi-k3",
                        Capabilities = ExpertMemberCapabilities.Propose |
                                       ExpertMemberCapabilities.Critique
                    },
                    new ExpertGroupMemberDefinition
                    {
                        MemberId = "backend",
                        Role = "backend-expert",
                        TemplateId = "backend-expert",
                        RouteKey = "opencode/glm-5.2",
                        Capabilities = ExpertMemberCapabilities.Propose |
                                       ExpertMemberCapabilities.Critique
                    },
                    new ExpertGroupMemberDefinition
                    {
                        MemberId = "reviewer",
                        Role = "independent-reviewer",
                        TemplateId = "reviewer",
                        RouteKey = "opencode/qwen3.8-max",
                        Capabilities = ExpertMemberCapabilities.Critique |
                                       ExpertMemberCapabilities.FinalReview
                    }
                ]
            }
        };

    private static string FormatIssues(DesignCouncilPlanCompilationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
}
