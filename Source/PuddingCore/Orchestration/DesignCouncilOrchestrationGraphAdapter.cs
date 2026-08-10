using System.Globalization;
using System.Text.Json;

namespace PuddingCode.Orchestration;

/// <summary>
/// Projects the MOA design-council template into the generic agent-orchestration graph contract.
/// It does not dispatch work; MOA-specific stages and visibility rules become typed nodes and edges.
/// </summary>
public sealed class DesignCouncilOrchestrationGraphAdapter
{
    private const string CanonicalRequestInputId = "canonical-request";
    private readonly AgentOrchestrationGraphCompiler _compiler = new();

    public AgentOrchestrationCompilationResult Compile(
        SubAgentOrchestrationPlan plan,
        string revisionId,
        int revision = 1,
        string? parentRevisionId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var adapterIssues = ValidateSource(plan, revisionId);
        if (adapterIssues.Count > 0)
        {
            return new AgentOrchestrationCompilationResult
            {
                Issues = adapterIssues
            };
        }

        var orderedStages = plan.Stages.OrderBy(stage => stage.Order).ToArray();
        var stageById = orderedStages.ToDictionary(stage => stage.StageId, StringComparer.OrdinalIgnoreCase);
        var workItems = plan.WorkItems.ToArray();
        var nodes = new List<AgentOrchestrationNodeDefinition>(orderedStages.Length + workItems.Length);
        nodes.AddRange(orderedStages.Select(CreateGateNode));
        nodes.AddRange(workItems.Select(CreateWorkNode));

        var edges = new List<AgentOrchestrationEdgeDefinition>();
        foreach (var stage in orderedStages)
        {
            var stageWorkItems = workItems.Where(item => IdEquals(item.StageId, stage.StageId)).ToArray();
            foreach (var workItem in stageWorkItems)
            {
                edges.Add(CreateControlEdge(
                    workItem.WorkItemId,
                    stage.StageId,
                    AgentOrchestrationEdgeCondition.OnCompletion,
                    "work-terminal"));

                foreach (var dependencyStageId in stage.DependsOnStageIds)
                {
                    edges.Add(CreateControlEdge(
                        dependencyStageId,
                        workItem.WorkItemId,
                        AgentOrchestrationEdgeCondition.OnSuccess,
                        "stage-release"));
                }
            }
        }

        foreach (var target in workItems)
        {
            foreach (var source in ResolveVisibleSources(target, workItems, stageById))
            {
                var targetInput = IdEquals(source.WorkItemId, target.TargetWorkItemId)
                    ? "targetProposal"
                    : source.Kind == SubAgentOrchestrationStageKind.Research
                        ? "researchOutputs"
                        : "priorOutputs";
                var aggregation = targetInput == "targetProposal"
                    ? AgentOrchestrationDataAggregation.Replace
                    : AgentOrchestrationDataAggregation.Append;
                edges.Add(CreateDataEdge(source.WorkItemId, target.WorkItemId, targetInput, aggregation));
            }
        }

        var definition = new AgentOrchestrationGraphDefinition
        {
            GraphId = plan.PlanId,
            RevisionId = revisionId,
            Revision = revision,
            ParentRevisionId = parentRevisionId,
            WorkspaceId = plan.DesignRequest.WorkspaceId,
            RootSessionId = plan.DesignRequest.ParentSessionId,
            CreatedByAgentId = plan.DesignRequest.RequestedByAgentId,
            Objective = plan.DesignRequest.ProblemStatement,
            RequiresExplicitActivation = plan.RequiresExplicitActivation,
            MaxConcurrency = plan.MaxConcurrency,
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = CanonicalRequestInputId,
                    Contract = new AgentOrchestrationDataContract
                    {
                        DataType = AgentOrchestrationDataTypes.Json,
                        MediaTypes = ["application/json"],
                        Deliveries = [AgentOrchestrationValueDelivery.Inline]
                    },
                    DefaultValue = new AgentOrchestrationValueEnvelope
                    {
                        DataType = AgentOrchestrationDataTypes.Json,
                        ContentType = "application/json",
                        InlineValue = JsonSerializer.SerializeToElement(
                            plan.DesignRequest,
                            AgentOrchestrationJson.CreateSerializerOptions())
                    }
                }
            ],
            Nodes = nodes.AsReadOnly(),
            Edges = edges.AsReadOnly(),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["templateKind"] = "moa.design-council",
                ["sourcePlanId"] = plan.PlanId,
                ["expertGroupId"] = plan.ExpertGroupId
            },
            CreatedAtUtc = plan.CompiledAtUtc
        };

        return _compiler.Compile(definition);
    }

    private static List<AgentOrchestrationValidationIssue> ValidateSource(
        SubAgentOrchestrationPlan plan,
        string revisionId)
    {
        var issues = new List<AgentOrchestrationValidationIssue>();
        if (plan.Status != SubAgentOrchestrationPlanStatus.Draft)
        {
            issues.Add(new(
                "adapter.plan_status_invalid",
                "Only a draft MOA plan can be compiled into a new graph revision.",
                "status"));
        }

        if (plan.AllowFallback)
            issues.Add(new("adapter.fallback_not_allowed", "MOA graph routes must remain exact.", "allowFallback"));
        if (string.IsNullOrWhiteSpace(revisionId))
            issues.Add(new("graph.revision_id_required", "RevisionId is required.", "revisionId"));
        if (plan.DesignRequest is null)
            issues.Add(new("adapter.design_request_required", "The MOA plan must contain its canonical design request.", "designRequest"));
        if (plan.Stages is null || plan.Stages.Count == 0)
            issues.Add(new("adapter.stages_required", "The MOA plan must contain stages.", "stages"));
        if (plan.WorkItems is null || plan.WorkItems.Count == 0)
            issues.Add(new("adapter.work_items_required", "The MOA plan must contain work items.", "workItems"));
        return issues;
    }

    private static AgentOrchestrationNodeDefinition CreateGateNode(SubAgentOrchestrationStage stage)
        => new()
        {
            NodeId = stage.StageId,
            Kind = AgentOrchestrationNodeKind.Gate,
            Title = $"{stage.Kind} gate",
            Objective = $"Evaluate the {stage.Gate} policy before releasing dependent work.",
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = AgentOrchestrationComponentTypes.Gate,
                Version = "1"
            },
            Gate = new AgentOrchestrationGateDefinition
            {
                EvaluatorId = ToGateEvaluatorId(stage.Gate),
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requiredSuccessfulItems"] = stage.RequiredSuccessfulItems.ToString(CultureInfo.InvariantCulture),
                    ["requiredDistinctRoutes"] = stage.RequiredDistinctRoutes.ToString(CultureInfo.InvariantCulture),
                    ["requiredCritiquesPerProposal"] = stage.RequiredCritiquesPerProposal.ToString(CultureInfo.InvariantCulture),
                    ["pauseOnCriticalContextGap"] = stage.PauseOnCriticalContextGap.ToString(CultureInfo.InvariantCulture)
                }
            },
            ExpectedOutputContract = "gateDecision, reason, blockingQuestions",
            FailureBehavior = stage.PauseOnCriticalContextGap
                ? AgentOrchestrationFailureBehavior.AwaitDecision
                : AgentOrchestrationFailureBehavior.FailRun,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["moa.stageKind"] = stage.Kind.ToString(),
                ["moa.stageOrder"] = stage.Order.ToString(CultureInfo.InvariantCulture)
            }
        };

    private static AgentOrchestrationNodeDefinition CreateWorkNode(SubAgentOrchestrationWorkItem workItem)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["moa.stageId"] = workItem.StageId,
            ["moa.stageKind"] = workItem.Kind.ToString(),
            ["moa.memberId"] = workItem.MemberId,
            ["moa.contextVisibility"] = workItem.ContextVisibility.ToString()
        };
        if (!string.IsNullOrWhiteSpace(workItem.TargetWorkItemId))
            metadata["moa.targetWorkItemId"] = workItem.TargetWorkItemId;

        return new AgentOrchestrationNodeDefinition
        {
            NodeId = workItem.WorkItemId,
            Kind = AgentOrchestrationNodeKind.SubAgent,
            Title = $"{workItem.Kind}: {workItem.Role}",
            Objective = workItem.Instruction,
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = AgentOrchestrationComponentTypes.SubAgent,
                Version = "1"
            },
            Executor = new AgentOrchestrationExecutorBinding
            {
                Kind = AgentOrchestrationExecutorKind.SubAgent,
                Role = workItem.Role,
                TemplateId = workItem.TemplateId,
                RouteKey = workItem.RouteKey
            },
            GraphInputBindings =
            [
                new AgentOrchestrationGraphInputBinding
                {
                    InputId = CanonicalRequestInputId,
                    TargetPortId = "request"
                }
            ],
            ExpectedOutputContract = workItem.ExpectedOutputContract,
            PermissionMode = workItem.IsReadOnly
                ? AgentOrchestrationPermissionMode.ReadOnly
                : AgentOrchestrationPermissionMode.ExplicitWrite,
            FailureBehavior = AgentOrchestrationFailureBehavior.FailRun,
            MaxAttempts = 1,
            Metadata = metadata
        };
    }

    private static IEnumerable<SubAgentOrchestrationWorkItem> ResolveVisibleSources(
        SubAgentOrchestrationWorkItem target,
        IReadOnlyList<SubAgentOrchestrationWorkItem> workItems,
        IReadOnlyDictionary<string, SubAgentOrchestrationStage> stageById)
    {
        return target.ContextVisibility switch
        {
            SubAgentWorkItemContextVisibility.CanonicalRequestOnly => Array.Empty<SubAgentOrchestrationWorkItem>(),
            SubAgentWorkItemContextVisibility.CanonicalRequestAndResearch => workItems
                .Where(item => item.Kind == SubAgentOrchestrationStageKind.Research),
            SubAgentWorkItemContextVisibility.TargetProposalAndEvidence => workItems
                .Where(item => item.Kind == SubAgentOrchestrationStageKind.Research ||
                               IdEquals(item.WorkItemId, target.TargetWorkItemId)),
            SubAgentWorkItemContextVisibility.AllPriorOutputs => workItems
                .Where(item => stageById[item.StageId].Order < stageById[target.StageId].Order),
            _ => throw new ArgumentOutOfRangeException(nameof(target.ContextVisibility), target.ContextVisibility, null)
        };
    }

    private static AgentOrchestrationEdgeDefinition CreateControlEdge(
        string fromNodeId,
        string toNodeId,
        AgentOrchestrationEdgeCondition condition,
        string purpose)
        => new()
        {
            EdgeId = $"{fromNodeId}=>{toNodeId}/control/{purpose}",
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Kind = AgentOrchestrationEdgeKind.Control,
            Condition = condition
        };

    private static AgentOrchestrationEdgeDefinition CreateDataEdge(
        string fromNodeId,
        string toNodeId,
        string targetInput,
        AgentOrchestrationDataAggregation aggregation)
        => new()
        {
            EdgeId = $"{fromNodeId}=>{toNodeId}/data",
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Kind = AgentOrchestrationEdgeKind.Data,
            Condition = AgentOrchestrationEdgeCondition.OnSuccess,
            Bindings =
            [
                new AgentOrchestrationDataBinding
                {
                    SourcePortId = "result",
                    SourcePath = "$",
                    TargetPortId = "context",
                    TargetKey = targetInput,
                    Aggregation = aggregation
                }
            ]
        };

    private static string ToGateEvaluatorId(SubAgentOrchestrationGateKind gate)
        => gate switch
        {
            SubAgentOrchestrationGateKind.ContextResolved => "moa.context-resolved/v1",
            SubAgentOrchestrationGateKind.EvidenceAvailable => "moa.evidence-available/v1",
            SubAgentOrchestrationGateKind.ProposalQuorum => "moa.proposal-quorum/v1",
            SubAgentOrchestrationGateKind.CritiqueCoverage => "moa.critique-coverage/v1",
            SubAgentOrchestrationGateKind.ChairSynthesis => "moa.chair-synthesis/v1",
            SubAgentOrchestrationGateKind.IndependentFinalReview => "moa.independent-final-review/v1",
            _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, null)
        };

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
