using PuddingCode.Orchestration;
using PuddingRuntime.Services.Orchestration;

namespace PuddingRuntimeTests.Services.Orchestration;

[TestClass]
public sealed class ImagePreviewOrchestrationNodeExecutorTests
{
    [TestMethod]
    public async Task Execute_ResolvesUpstreamArtifactAndPassesItThrough()
    {
        var generate = Node("generate", AgentOrchestrationComponentTypes.ImageGenerate, "generate_image");
        var preview = Node("preview", AgentOrchestrationComponentTypes.ImagePreview, "preview_image");
        var definition = new AgentOrchestrationGraphDefinition
        {
            GraphId = "image-chain",
            RevisionId = "image-chain/r001",
            WorkspaceId = "default",
            RootSessionId = "root",
            CreatedByAgentId = "admin",
            Objective = "Generate and preview an image.",
            Nodes = [generate, preview],
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "generate-preview",
                    FromNodeId = generate.NodeId,
                    ToNodeId = preview.NodeId,
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "images",
                            TargetPortId = "images"
                        }
                    ]
                }
            ]
        };
        var run = new AgentOrchestrationRunSnapshot
        {
            RunId = "run-image-chain",
            GraphId = definition.GraphId,
            RevisionId = definition.RevisionId,
            WorkspaceId = definition.WorkspaceId,
            RootSessionId = definition.RootSessionId,
            RequestedByAgentId = "admin",
            Status = AgentOrchestrationRunStatus.Active,
            Version = 5,
            HeadSequence = 8,
            MaxConcurrency = 1,
            Nodes =
            [
                Snapshot(generate, AgentOrchestrationNodeRunStatus.Completed, "vision-output-001"),
                Snapshot(preview, AgentOrchestrationNodeRunStatus.Running, null)
            ],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var executor = new ImagePreviewOrchestrationNodeExecutor();

        var result = await executor.ExecuteAsync(new AgentOrchestrationNodeExecutionContext
        {
            Definition = definition,
            Run = run,
            Node = preview,
            Claim = new AgentOrchestrationNodeClaim
            {
                RunId = run.RunId,
                NodeId = preview.NodeId,
                ClaimId = "claim-preview",
                WorkerId = "worker",
                Attempt = 1,
                FencingToken = 1,
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                RunVersion = run.Version
            }
        }, CancellationToken.None);

        Assert.AreEqual("vision-output-001", result.ArtifactReference);
        Assert.AreEqual("vision-output-001", result.Outputs["images"].Artifacts.Single().ArtifactId);
        Assert.AreEqual("Previewed 1 image artifact.", result.Summary);
    }

    private static AgentOrchestrationNodeDefinition Node(
        string nodeId,
        string componentType,
        string toolId)
        => new()
        {
            NodeId = nodeId,
            Kind = AgentOrchestrationNodeKind.Tool,
            Title = nodeId,
            Objective = nodeId,
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = componentType,
                Version = "1"
            },
            Executor = new AgentOrchestrationExecutorBinding
            {
                Kind = AgentOrchestrationExecutorKind.Tool,
                ToolId = toolId
            },
            ExpectedOutputContract = AgentOrchestrationDataTypes.Artifact
        };

    private static AgentOrchestrationNodeRunSnapshot Snapshot(
        AgentOrchestrationNodeDefinition node,
        AgentOrchestrationNodeRunStatus status,
        string? artifactReference)
        => new()
        {
            NodeId = node.NodeId,
            Kind = node.Kind,
            Status = status,
            Attempt = 1,
            MaxAttempts = 1,
            FencingToken = 1,
            ArtifactReference = artifactReference,
            Outputs = artifactReference is null
                ? new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal)
                : new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal)
                {
                    ["images"] = new()
                    {
                        DataType = AgentOrchestrationDataTypes.Artifact,
                        Artifacts =
                        [
                            new AgentOrchestrationArtifactReference
                            {
                                ArtifactId = artifactReference,
                                ContentType = "image/png"
                            }
                        ]
                    }
                },
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
}
