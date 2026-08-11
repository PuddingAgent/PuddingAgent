using System.Text.Json;
using PuddingCode.Orchestration;
using PuddingRuntime.Services.Orchestration;

namespace PuddingRuntimeTests.Services.Orchestration;

[TestClass]
public sealed class AgentOrchestrationNodeInputResolverTests
{
    [TestMethod]
    public void ResolveInlineText_ReadsCommittedUpstreamOutputPort()
    {
        var source = Node("copy", "pudding.agent.subagent", AgentOrchestrationNodeKind.SubAgent);
        var target = Node("storyboard", "pudding.agent.subagent", AgentOrchestrationNodeKind.SubAgent);
        var definition = new AgentOrchestrationGraphDefinition
        {
            GraphId = "chain",
            RevisionId = "chain/r001",
            WorkspaceId = "default",
            RootSessionId = "root",
            CreatedByAgentId = "admin",
            Objective = "chain",
            Nodes = [source, target],
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "copy-storyboard",
                    FromNodeId = source.NodeId,
                    ToNodeId = target.NodeId,
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            TargetPortId = "request",
                            Aggregation = AgentOrchestrationDataAggregation.Replace
                        }
                    ]
                }
            ]
        };
        var run = new AgentOrchestrationRunSnapshot
        {
            RunId = "run-chain",
            GraphId = definition.GraphId,
            RevisionId = definition.RevisionId,
            WorkspaceId = definition.WorkspaceId,
            RootSessionId = definition.RootSessionId,
            RequestedByAgentId = "admin",
            Status = AgentOrchestrationRunStatus.Active,
            Inputs = new Dictionary<string, AgentOrchestrationValueEnvelope>
            {
                ["brief"] = Content("this graph input is replaced")
            },
            Nodes =
            [
                Snapshot(source, AgentOrchestrationNodeRunStatus.Completed, new Dictionary<string, AgentOrchestrationValueEnvelope>
                {
                    ["result"] = Content("策划案正文")
                }),
                Snapshot(target, AgentOrchestrationNodeRunStatus.Running, new Dictionary<string, AgentOrchestrationValueEnvelope>())
            ]
        };
        target = target with
        {
            GraphInputBindings =
            [
                new AgentOrchestrationGraphInputBinding { InputId = "brief", TargetPortId = "request" }
            ]
        };
        definition = definition with { Nodes = [source, target] };

        var resolved = AgentOrchestrationNodeInputResolver.ResolveInlineText(new AgentOrchestrationNodeExecutionContext
        {
            Definition = definition,
            Run = run,
            Node = target,
            Claim = new AgentOrchestrationNodeClaim
            {
                RunId = run.RunId,
                NodeId = target.NodeId,
                ClaimId = "claim",
                WorkerId = "worker",
                Attempt = 1,
                FencingToken = 1,
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                RunVersion = 1
            }
        }, "request");

        Assert.AreEqual("策划案正文", resolved);
    }

    private static AgentOrchestrationNodeDefinition Node(
        string nodeId,
        string componentType,
        AgentOrchestrationNodeKind kind)
        => new()
        {
            NodeId = nodeId,
            Kind = kind,
            Title = nodeId,
            Objective = nodeId,
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = componentType,
                Version = "1"
            },
            ExpectedOutputContract = AgentOrchestrationDataTypes.Content
        };

    private static AgentOrchestrationNodeRunSnapshot Snapshot(
        AgentOrchestrationNodeDefinition node,
        AgentOrchestrationNodeRunStatus status,
        IReadOnlyDictionary<string, AgentOrchestrationValueEnvelope> outputs)
        => new()
        {
            NodeId = node.NodeId,
            Kind = node.Kind,
            Status = status,
            Attempt = 1,
            MaxAttempts = 1,
            FencingToken = 1,
            Outputs = outputs,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    private static AgentOrchestrationValueEnvelope Content(string text) => new()
    {
        DataType = AgentOrchestrationDataTypes.Content,
        ContentType = "text/plain",
        InlineValue = JsonSerializer.SerializeToElement(text)
    };
}
