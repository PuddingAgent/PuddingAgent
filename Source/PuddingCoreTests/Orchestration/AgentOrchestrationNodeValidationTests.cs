using System.Text.Json;
using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

/// <summary>
/// S1 server-side node validation rules (doc 85 §5.1): unknown components, stale contract hashes,
/// duplicate node ids, empty node lists, invalid routes and kind-specific executor/gate rules.
/// </summary>
[TestClass]
public sealed class AgentOrchestrationNodeValidationTests
{
    private readonly AgentOrchestrationGraphCompiler _compiler = new();

    [TestMethod]
    public void Compile_RejectsDuplicateNodeIds()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model"),
            CreateSubAgentNode("worker", "provider/model-2")
        ]);

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_id_duplicate"));
    }

    [TestMethod]
    public void Compile_RejectsEmptyNodeList()
    {
        var definition = CreateDefinition([]);

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.nodes_required"));
    }

    [TestMethod]
    public void Compile_RejectsUnknownComponentAndStaleContractHash()
    {
        var unknown = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model") with
            {
                Component = new AgentOrchestrationComponentReference
                {
                    ComponentType = "missing.component",
                    Version = "9"
                }
            }
        ]);
        var stale = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model") with
            {
                Component = new AgentOrchestrationComponentReference
                {
                    ComponentType = AgentOrchestrationComponentTypes.SubAgent,
                    Version = "1",
                    ContractHash = "sha256:stale"
                }
            }
        ]);

        var unknownResult = _compiler.Compile(unknown);
        var staleResult = _compiler.Compile(stale);

        Assert.IsTrue(unknownResult.Issues.Any(issue => issue.Code == "graph.node_component_unknown"));
        Assert.IsTrue(staleResult.Issues.Any(issue => issue.Code == "graph.node_component_contract_mismatch"));
    }

    [TestMethod]
    public void Compile_RejectsInvalidRouteKey()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("worker", "not-a-route")
        ]);

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_route_invalid"));
    }

    [TestMethod]
    public void Compile_RejectsSubAgentWithoutFrozenRouteForActivationCandidate()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("worker", routeKey: null)
        ]);

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_route_required"));
    }

    [TestMethod]
    public void Compile_RejectsGateWithoutEvaluatorAndHumanInputWithExecutor()
    {
        var gateWithoutEvaluator = CreateDefinition(
        [
            new AgentOrchestrationNodeDefinition
            {
                NodeId = "gate",
                Kind = AgentOrchestrationNodeKind.Gate,
                Title = "Gate",
                Objective = "Evaluate the outcome.",
                Component = new AgentOrchestrationComponentReference
                {
                    ComponentType = AgentOrchestrationComponentTypes.Gate,
                    Version = "1"
                },
                ExpectedOutputContract = AgentOrchestrationDataTypes.Json
            }
        ]);
        var humanInputWithExecutor = CreateDefinition(
        [
            new AgentOrchestrationNodeDefinition
            {
                NodeId = "ask",
                Kind = AgentOrchestrationNodeKind.HumanInput,
                Title = "Ask",
                Objective = "Ask the user.",
                Component = new AgentOrchestrationComponentReference
                {
                    ComponentType = AgentOrchestrationComponentTypes.HumanInput,
                    Version = "1"
                },
                Executor = new AgentOrchestrationExecutorBinding
                {
                    Kind = AgentOrchestrationExecutorKind.SubAgent,
                    Role = "helper",
                    TemplateId = "helper",
                    RouteKey = "provider/model"
                },
                ExpectedOutputContract = AgentOrchestrationDataTypes.Content
            }
        ]);

        var gateResult = _compiler.Compile(gateWithoutEvaluator);
        var humanResult = _compiler.Compile(humanInputWithExecutor);

        Assert.IsTrue(gateResult.Issues.Any(issue => issue.Code == "graph.node_gate_required"));
        Assert.IsTrue(humanResult.Issues.Any(issue => issue.Code == "graph.node_executor_unexpected"));
    }

    [TestMethod]
    public void Compile_RejectsHumanInputWithGateAndSubAgentWithToolId()
    {
        var humanWithGate = CreateDefinition(
        [
            new AgentOrchestrationNodeDefinition
            {
                NodeId = "ask",
                Kind = AgentOrchestrationNodeKind.HumanInput,
                Title = "Ask",
                Objective = "Ask the user.",
                Component = new AgentOrchestrationComponentReference
                {
                    ComponentType = AgentOrchestrationComponentTypes.HumanInput,
                    Version = "1"
                },
                Gate = new AgentOrchestrationGateDefinition { EvaluatorId = "pudding.gate.approval/v1" },
                ExpectedOutputContract = AgentOrchestrationDataTypes.Content
            }
        ]);
        var subAgentWithTool = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model") with
            {
                Executor = CreateSubAgentNode("worker", "provider/model").Executor! with
                {
                    ToolId = "unexpected.tool"
                }
            }
        ]);

        var gateResult = _compiler.Compile(humanWithGate);
        var toolResult = _compiler.Compile(subAgentWithTool);

        Assert.IsTrue(gateResult.Issues.Any(issue => issue.Code == "graph.node_gate_unexpected"));
        Assert.IsTrue(toolResult.Issues.Any(issue => issue.Code == "graph.node_tool_unexpected"));
    }

    [TestMethod]
    public void Compile_RejectsRequiredPortUnboundAndSinglePortMultipleBindings()
    {
        // Sub-agent component requires the 'request' input port; an unbound node must fail.
        var unbound = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model") with
            {
                GraphInputBindings = []
            }
        ]);

        var unboundResult = _compiler.Compile(unbound);
        Assert.IsTrue(unboundResult.Issues.Any(issue => issue.Code == "graph.node_required_input_unbound"));
    }

    [TestMethod]
    public void Compile_RejectsExplicitWriteNodeUntilApprovalAwarePolicyExists()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model") with
            {
                PermissionMode = AgentOrchestrationPermissionMode.ExplicitWrite
            }
        ]);

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_write_not_allowed"));
    }

    private static AgentOrchestrationGraphDefinition CreateDefinition(
        IReadOnlyList<AgentOrchestrationNodeDefinition> nodes)
        => new()
        {
            GraphId = "graph-001",
            RevisionId = "graph-001/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Validate node definitions.",
            MaxConcurrency = 2,
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = "request",
                    Contract = new AgentOrchestrationDataContract
                    {
                        DataType = AgentOrchestrationDataTypes.Text,
                        MediaTypes = ["text/plain"],
                        Deliveries = [AgentOrchestrationValueDelivery.Inline]
                    }
                }
            ],
            Nodes = nodes
        };

    private static AgentOrchestrationNodeDefinition CreateSubAgentNode(string nodeId, string? routeKey)
        => new()
        {
            NodeId = nodeId,
            Kind = AgentOrchestrationNodeKind.SubAgent,
            Title = nodeId,
            Objective = $"Execute {nodeId}.",
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = AgentOrchestrationComponentTypes.SubAgent,
                Version = "1"
            },
            Executor = new AgentOrchestrationExecutorBinding
            {
                Kind = AgentOrchestrationExecutorKind.SubAgent,
                Role = "specialist",
                TemplateId = "specialist",
                RouteKey = routeKey
            },
            GraphInputBindings =
            [
                new AgentOrchestrationGraphInputBinding
                {
                    InputId = "request",
                    TargetPortId = "request"
                }
            ],
            ExpectedOutputContract = AgentOrchestrationDataTypes.Content
        };
}
