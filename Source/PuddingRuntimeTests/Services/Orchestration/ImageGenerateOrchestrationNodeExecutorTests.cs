using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Orchestration;
using PuddingRuntime.Services.Orchestration;

namespace PuddingRuntimeTests.Services.Orchestration;

[TestClass]
public sealed class ImageGenerateOrchestrationNodeExecutorTests
{
    [TestMethod]
    public async Task Execute_MapsPromptConfigurationAndStableIdempotencyKey()
    {
        var imageService = new RecordingImageGenerationService();
        var executor = new ImageGenerateOrchestrationNodeExecutor(imageService);
        var context = CreateContext();

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.AreEqual("vision-output-001", result.ArtifactReference);
        Assert.AreEqual("vision-output-001", result.Outputs["images"].Artifacts.Single().ArtifactId);
        Assert.AreEqual("image/png", result.Outputs["images"].Artifacts.Single().ContentType);
        Assert.AreEqual("a cinematic lighthouse", imageService.Request!.Prompt);
        Assert.AreEqual("4K", imageService.Request.Size);
        Assert.IsFalse(imageService.Request.Watermark);
        Assert.AreEqual("orchestration:run-image:image:2", imageService.Request.IdempotencyKey);
        Assert.AreEqual("reference-001", imageService.Request.ReferenceArtifactIds.Single());
    }

    private static AgentOrchestrationNodeExecutionContext CreateContext()
    {
        var node = new AgentOrchestrationNodeDefinition
        {
            NodeId = "image",
            Kind = AgentOrchestrationNodeKind.Tool,
            Title = "Generate",
            Objective = "Generate one image.",
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = AgentOrchestrationComponentTypes.ImageGenerate,
                Version = "1"
            },
            Executor = new AgentOrchestrationExecutorBinding
            {
                Kind = AgentOrchestrationExecutorKind.Tool,
                ToolId = "generate_image"
            },
            GraphInputBindings =
            [
                new AgentOrchestrationGraphInputBinding { InputId = "prompt", TargetPortId = "prompt" },
                new AgentOrchestrationGraphInputBinding { InputId = "reference", TargetPortId = "references" }
            ],
            ExpectedOutputContract = AgentOrchestrationDataTypes.Artifact,
            Configuration = new Dictionary<string, JsonElement>
            {
                ["size"] = JsonSerializer.SerializeToElement("4K"),
                ["watermark"] = JsonSerializer.SerializeToElement(false)
            }
        };
        var definition = new AgentOrchestrationGraphDefinition
        {
            GraphId = "graph-image",
            RevisionId = "graph-image/r001",
            WorkspaceId = "default",
            RootSessionId = "root",
            CreatedByAgentId = "admin",
            Objective = "Generate an image.",
            Nodes = [node]
        };
        return new AgentOrchestrationNodeExecutionContext
        {
            Definition = definition,
            Node = node,
            Run = new AgentOrchestrationRunSnapshot
            {
                RunId = "run-image",
                GraphId = definition.GraphId,
                RevisionId = definition.RevisionId,
                WorkspaceId = definition.WorkspaceId,
                RootSessionId = definition.RootSessionId,
                RequestedByAgentId = "admin",
                Status = AgentOrchestrationRunStatus.Active,
                Inputs = new Dictionary<string, AgentOrchestrationValueEnvelope>
                {
                    ["prompt"] = new()
                    {
                        DataType = AgentOrchestrationDataTypes.Content,
                        ContentType = "text/plain",
                        InlineValue = JsonSerializer.SerializeToElement("a cinematic lighthouse")
                    },
                    ["reference"] = new()
                    {
                        DataType = AgentOrchestrationDataTypes.Artifact,
                        Artifacts =
                        [
                            new AgentOrchestrationArtifactReference
                            {
                                ArtifactId = "reference-001",
                                ContentType = "image/png"
                            }
                        ]
                    }
                }
            },
            Claim = new AgentOrchestrationNodeClaim
            {
                RunId = "run-image",
                NodeId = "image",
                ClaimId = "claim-image",
                WorkerId = "worker",
                Attempt = 2,
                FencingToken = 2,
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                RunVersion = 3
            }
        };
    }

    private sealed class RecordingImageGenerationService : IImageGenerationService
    {
        public ImageGenerationRequest? Request { get; private set; }

        public Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new ImageGenerationResult
            {
                ProviderId = "image-provider",
                ModelId = "image-model",
                Artifacts =
                [
                    new ImageGenerationArtifact
                    {
                        ArtifactId = "vision-output-001",
                        MimeType = "image/png"
                    }
                ]
            });
        }
    }
}
