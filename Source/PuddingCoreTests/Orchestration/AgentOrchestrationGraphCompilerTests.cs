using System.Text.Json;
using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

[TestClass]
public sealed class AgentOrchestrationGraphCompilerTests
{
    private readonly AgentOrchestrationGraphCompiler _compiler = new();

    [TestMethod]
    public void Compile_ProducesImmutableActivatableDagAndCamelCaseJson()
    {
        var nodes = new List<AgentOrchestrationNodeDefinition>
        {
            CreateSubAgentNode("research", "deepseek/deepseek-v4-flash"),
            CreateSubAgentNode("design", "opencode/kimi-k3")
        };
        var definition = CreateDefinition(nodes) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "research-to-design",
                    FromNodeId = "research",
                    ToNodeId = "design",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            TargetPortId = "context",
                            TargetKey = "researchOutputs"
                        }
                    ]
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
        CollectionAssert.AreEqual(new[] { "research", "design" }, result.TopologicalNodeIds.ToArray());
        nodes[0] = CreateSubAgentNode("mutated", "provider/model");
        Assert.AreEqual("research", result.Definition!.Nodes[0].NodeId);

        var json = JsonSerializer.Serialize(result.Definition, AgentOrchestrationJson.CreateSerializerOptions());
        StringAssert.Contains(json, "\"schemaVersion\":\"pudding.agent-orchestration/v2\"");
        StringAssert.Contains(json, "\"kind\":\"subAgent\"");
        StringAssert.Contains(json, "\"permissionMode\":\"readOnly\"");
        StringAssert.Contains(json, "\"contractHash\":\"sha256:");

        var roundTrip = JsonSerializer.Deserialize<AgentOrchestrationGraphDefinition>(
            json,
            AgentOrchestrationJson.CreateSerializerOptions());
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(AgentOrchestrationNodeKind.SubAgent, roundTrip.Nodes[0].Kind);
    }

    [TestMethod]
    public void Compile_RejectsCyclesAcrossControlAndDataEdges()
    {
        var definition = CreateDefinition(
            [
                CreateSubAgentNode("a", "provider/model-a"),
                CreateSubAgentNode("b", "provider/model-b")
            ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control
                },
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "b-to-a",
                    FromNodeId = "b",
                    ToNodeId = "a",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            TargetPortId = "context",
                            TargetKey = "feedback"
                        }
                    ]
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.cycle_detected"));
    }

    [TestMethod]
    public void Compile_RequiresFrozenSubAgentRouteForActivationCandidate()
    {
        var definition = CreateDefinition([CreateSubAgentNode("worker", routeKey: null)]);

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_route_required"));

        var draftResult = _compiler.Compile(
            definition,
            new AgentOrchestrationValidationOptions { RequireFrozenRoutes = false });
        Assert.IsTrue(draftResult.Success, FormatIssues(draftResult));
    }

    [TestMethod]
    public void Compile_RejectsWriteNodeUntilApprovalAwareActivationIsEnabled()
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
        var approved = _compiler.Compile(
            definition,
            new AgentOrchestrationValidationOptions { AllowExplicitWriteNodes = true });
        Assert.IsTrue(approved.Success, FormatIssues(approved));
    }

    [TestMethod]
    public void Compile_RequiresParentRevisionForEdits()
    {
        var definition = CreateDefinition([CreateSubAgentNode("worker", "provider/model")]) with
        {
            Revision = 2,
            ParentRevisionId = null
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.parent_revision_required"));
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
            Objective = "Research and produce a design.",
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
                    },
                    DefaultValue = new AgentOrchestrationValueEnvelope
                    {
                        DataType = AgentOrchestrationDataTypes.Text,
                        ContentType = "text/plain",
                        InlineValue = JsonSerializer.SerializeToElement("Design a system.")
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
            ExpectedOutputContract = "result"
        };

    private static string FormatIssues(AgentOrchestrationCompilationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
}
