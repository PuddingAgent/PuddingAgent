using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

[TestClass]
public sealed class DesignCouncilOrchestrationGraphAdapterTests
{
    private readonly DesignCouncilPlanCompiler _planCompiler = new();
    private readonly DesignCouncilOrchestrationGraphAdapter _adapter = new();

    [TestMethod]
    public void Compile_MapsMoaStagesAndWorkItemsToGenericGraph()
    {
        var planResult = _planCompiler.Compile(DesignCouncilPlanCompilerTests.CreateCompileRequest());
        Assert.IsTrue(planResult.Success);

        var result = _adapter.Compile(planResult.Plan!, "moa-design-001/r001");

        Assert.IsTrue(result.Success, FormatIssues(result));
        var graph = result.Definition!;
        Assert.AreEqual(AgentOrchestrationSchemas.GraphDefinitionV2, graph.SchemaVersion);
        Assert.AreEqual("moa.design-council", graph.Metadata["templateKind"]);
        Assert.IsTrue(graph.RequiresExplicitActivation);
        Assert.AreEqual(planResult.Plan!.Stages.Count + planResult.Plan.WorkItems.Count, graph.Nodes.Count);
        Assert.AreEqual(planResult.Plan.Stages.Count, graph.Nodes.Count(node => node.Kind == AgentOrchestrationNodeKind.Gate));
        Assert.AreEqual(planResult.Plan.WorkItems.Count, graph.Nodes.Count(node => node.Kind == AgentOrchestrationNodeKind.SubAgent));
        Assert.IsTrue(graph.Nodes
            .Where(node => node.Kind == AgentOrchestrationNodeKind.SubAgent)
            .All(node => node.PermissionMode == AgentOrchestrationPermissionMode.ReadOnly &&
                         node.Executor?.RouteKey?.Contains('/') == true));
    }

    [TestMethod]
    public void Compile_PreservesIndependentProposalVisibility()
    {
        var plan = _planCompiler.Compile(DesignCouncilPlanCompilerTests.CreateCompileRequest()).Plan!;
        var graph = _adapter.Compile(plan, "moa-design-001/r001").Definition!;
        var proposalIds = plan.WorkItems
            .Where(item => item.Kind == SubAgentOrchestrationStageKind.IndependentProposal)
            .Select(item => item.WorkItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposalToProposalEdges = graph.Edges.Where(edge =>
            edge.Kind == AgentOrchestrationEdgeKind.Data &&
            proposalIds.Contains(edge.FromNodeId) &&
            proposalIds.Contains(edge.ToNodeId));

        Assert.IsFalse(proposalToProposalEdges.Any());
        foreach (var proposalId in proposalIds)
        {
            var sources = graph.Edges
                .Where(edge => edge.Kind == AgentOrchestrationEdgeKind.Data && edge.ToNodeId == proposalId)
                .Select(edge => plan.WorkItems.Single(item => item.WorkItemId == edge.FromNodeId))
                .ToArray();
            Assert.IsTrue(sources.All(source => source.Kind == SubAgentOrchestrationStageKind.Research));
        }
    }

    [TestMethod]
    public void Compile_ExposesOnlyTargetProposalAndEvidenceToEachCritic()
    {
        var plan = _planCompiler.Compile(DesignCouncilPlanCompilerTests.CreateCompileRequest()).Plan!;
        var graph = _adapter.Compile(plan, "moa-design-001/r001").Definition!;
        var critiques = plan.WorkItems.Where(item => item.Kind == SubAgentOrchestrationStageKind.CrossCritique);

        foreach (var critique in critiques)
        {
            var sourceIds = graph.Edges
                .Where(edge => edge.Kind == AgentOrchestrationEdgeKind.Data && edge.ToNodeId == critique.WorkItemId)
                .Select(edge => edge.FromNodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.IsNotNull(critique.TargetWorkItemId);
            Assert.Contains(critique.TargetWorkItemId, sourceIds);
            Assert.IsTrue(sourceIds
                .Where(sourceId => !string.Equals(sourceId, critique.TargetWorkItemId, StringComparison.OrdinalIgnoreCase))
                .Select(sourceId => plan.WorkItems.Single(item => item.WorkItemId == sourceId))
                .All(source => source.Kind == SubAgentOrchestrationStageKind.Research));
        }
    }

    [TestMethod]
    public void Compile_UsesStageGatesAsControlDependencies()
    {
        var plan = _planCompiler.Compile(DesignCouncilPlanCompilerTests.CreateCompileRequest()).Plan!;
        var graph = _adapter.Compile(plan, "moa-design-001/r001").Definition!;

        foreach (var stage in plan.Stages)
        {
            foreach (var workItem in plan.WorkItems.Where(item => item.StageId == stage.StageId))
            {
                Assert.IsTrue(graph.Edges.Any(edge =>
                    edge.Kind == AgentOrchestrationEdgeKind.Control &&
                    edge.Condition == AgentOrchestrationEdgeCondition.OnCompletion &&
                    edge.FromNodeId == workItem.WorkItemId &&
                    edge.ToNodeId == stage.StageId));

                foreach (var dependencyStageId in stage.DependsOnStageIds)
                {
                    Assert.IsTrue(graph.Edges.Any(edge =>
                        edge.Kind == AgentOrchestrationEdgeKind.Control &&
                        edge.Condition == AgentOrchestrationEdgeCondition.OnSuccess &&
                        edge.FromNodeId == dependencyStageId &&
                        edge.ToNodeId == workItem.WorkItemId));
                }
            }
        }
    }

    private static string FormatIssues(AgentOrchestrationCompilationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
}
