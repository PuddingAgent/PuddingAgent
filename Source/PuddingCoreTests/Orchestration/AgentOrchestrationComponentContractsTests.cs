using System.Text.Json;
using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

[TestClass]
public sealed class AgentOrchestrationComponentContractsTests
{
    [TestMethod]
    public void DefaultRegistry_ExposesVersionedComponentsAndTriggersWithStableHashes()
    {
        var first = AgentOrchestrationComponentRegistry.Default;
        Assert.IsTrue(first.TryResolveComponent(AgentOrchestrationComponentTypes.SubAgent, "1", out var subAgent));
        Assert.IsTrue(first.TryResolveTrigger(AgentOrchestrationTriggerTypes.Manual, "1", out var manual));
        StringAssert.StartsWith(subAgent.ContractHash, "sha256:");
        StringAssert.StartsWith(manual.ContractHash, "sha256:");

        var rebuilt = new AgentOrchestrationComponentRegistry(
            first.Components.Select(component => component.Descriptor),
            first.Triggers.Select(trigger => trigger.Descriptor));

        Assert.IsTrue(rebuilt.TryResolveComponent(AgentOrchestrationComponentTypes.SubAgent, "1", out var rebuiltSubAgent));
        Assert.IsTrue(rebuilt.TryResolveTrigger(AgentOrchestrationTriggerTypes.Manual, "1", out var rebuiltManual));
        Assert.AreEqual(subAgent.ContractHash, rebuiltSubAgent.ContractHash);
        Assert.AreEqual(manual.ContractHash, rebuiltManual.ContractHash);
    }

    [TestMethod]
    public void DefaultRegistry_ExposesTypedImageGenerationComponent()
    {
        Assert.IsTrue(AgentOrchestrationComponentRegistry.Default.TryResolveComponent(
            AgentOrchestrationComponentTypes.ImageGenerate,
            "1",
            out var imageGenerate));

        Assert.AreEqual(AgentOrchestrationNodeKind.Tool, imageGenerate.Descriptor.NodeKind);
        Assert.AreEqual("pudding.runtime.image-generate/v1", imageGenerate.Descriptor.ExecutorId);
        Assert.AreEqual("prompt", imageGenerate.Descriptor.InputPorts.Single(port => port.Required).PortId);
        var output = imageGenerate.Descriptor.OutputPorts.Single();
        Assert.AreEqual(AgentOrchestrationDataTypes.Artifact, output.Contract.DataType);
        Assert.IsTrue(output.Contract.MediaTypes.Contains("image/*"));
        Assert.IsTrue(output.Contract.Deliveries.Contains(AgentOrchestrationValueDelivery.Artifact));
    }

    [TestMethod]
    public void DefaultRegistry_ExposesTypedImagePreviewComponent()
    {
        Assert.IsTrue(AgentOrchestrationComponentRegistry.Default.TryResolveComponent(
            AgentOrchestrationComponentTypes.ImagePreview,
            "1",
            out var preview));

        Assert.AreEqual(AgentOrchestrationNodeKind.Tool, preview.Descriptor.NodeKind);
        Assert.AreEqual("pudding.runtime.image-preview/v1", preview.Descriptor.ExecutorId);
        var input = preview.Descriptor.InputPorts.Single();
        var output = preview.Descriptor.OutputPorts.Single();
        Assert.AreEqual("images", input.PortId);
        Assert.AreEqual(AgentOrchestrationPortCardinality.Many, input.Contract.Cardinality);
        Assert.IsTrue(AgentOrchestrationPortCompatibility.IsCompatible(
            AgentOrchestrationComponentRegistry.Default.Components
                .Single(component => component.Descriptor.ComponentType == AgentOrchestrationComponentTypes.ImageGenerate)
                .Descriptor.OutputPorts.Single(),
            input));
        Assert.AreEqual("images", output.PortId);
    }

    [TestMethod]
    public void Registry_RejectsDuplicatePortIdsBeforeTheyReachTheEditor()
    {
        var duplicatePort = Port(
            "value",
            AgentOrchestrationDataTypes.Text,
            AgentOrchestrationPortCardinality.One,
            ["text/plain"],
            AgentOrchestrationValueDelivery.Inline);
        var descriptor = new AgentOrchestrationComponentDescriptor
        {
            ComponentType = "test.invalid",
            Version = "1",
            DisplayName = "Invalid",
            Category = "test",
            NodeKind = AgentOrchestrationNodeKind.Tool,
            ExecutorId = "test.invalid/v1",
            InputPorts = [duplicatePort, duplicatePort]
        };

        Assert.Throws<ArgumentException>(() => new AgentOrchestrationComponentRegistry([descriptor]));
    }

    [TestMethod]
    public void PortCompatibility_EnforcesMediaDeliveryAndCardinality()
    {
        var imageOutput = Port(
            "image",
            AgentOrchestrationDataTypes.Artifact,
            AgentOrchestrationPortCardinality.One,
            ["image/png"],
            AgentOrchestrationValueDelivery.Artifact);
        var imageInput = Port(
            "source",
            AgentOrchestrationDataTypes.Artifact,
            AgentOrchestrationPortCardinality.Many,
            ["image/*"],
            AgentOrchestrationValueDelivery.Artifact);
        var audioInput = imageInput with
        {
            Contract = imageInput.Contract with { MediaTypes = ["audio/*"] }
        };
        var singleInput = imageInput with
        {
            Contract = imageInput.Contract with { Cardinality = AgentOrchestrationPortCardinality.One }
        };
        var manyOutput = imageOutput with
        {
            Contract = imageOutput.Contract with { Cardinality = AgentOrchestrationPortCardinality.Many }
        };

        Assert.IsTrue(AgentOrchestrationPortCompatibility.IsCompatible(imageOutput, imageInput));
        Assert.IsFalse(AgentOrchestrationPortCompatibility.IsCompatible(imageOutput, audioInput));
        Assert.IsFalse(AgentOrchestrationPortCompatibility.IsCompatible(manyOutput, singleInput));
    }

    [TestMethod]
    public void Compile_FreezesMultimodalComponentsAndTriggerContracts()
    {
        var compiler = new AgentOrchestrationGraphCompiler(CreateMediaRegistry(targetMediaType: "image/*"));

        var result = compiler.Compile(CreateMediaGraph());

        Assert.IsTrue(result.Success, FormatIssues(result));
        Assert.IsTrue(result.Definition!.Nodes.All(node => node.Component.ContractHash?.StartsWith("sha256:") == true));
        StringAssert.StartsWith(result.Definition.Triggers[0].Trigger.ContractHash!, "sha256:");
        Assert.AreEqual("artifact-image-001", result.Definition.Inputs[0].DefaultValue!.Artifacts[0].ArtifactId);

        var json = JsonSerializer.Serialize(result.Definition, AgentOrchestrationJson.CreateSerializerOptions());
        StringAssert.Contains(json, "\"schemaVersion\":\"pudding.agent-orchestration/v2\"");
        StringAssert.Contains(json, "\"mediaTypes\":[\"image/png\"]");
        Assert.IsFalse(json.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Compile_RejectsIncompatibleMultimodalPorts()
    {
        var compiler = new AgentOrchestrationGraphCompiler(CreateMediaRegistry(targetMediaType: "audio/*"));

        var result = compiler.Compile(CreateMediaGraph());

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_ports_incompatible"));
    }

    [TestMethod]
    public void Compile_RejectsArtifactContentTypeOutsideInputContract()
    {
        var definition = CreateMediaGraph();
        var invalidInput = definition.Inputs[0] with
        {
            DefaultValue = definition.Inputs[0].DefaultValue! with
            {
                Artifacts =
                [
                    definition.Inputs[0].DefaultValue!.Artifacts[0] with
                    {
                        ContentType = "audio/wav"
                    }
                ]
            }
        };
        var compiler = new AgentOrchestrationGraphCompiler(CreateMediaRegistry(targetMediaType: "image/*"));

        var result = compiler.Compile(definition with { Inputs = [invalidInput] });

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.value_media_type_mismatch"));
    }

    [TestMethod]
    public void Compile_RejectsUnknownComponentAndStaleContractHash()
    {
        var definition = CreateMediaGraph();
        var unknown = definition with
        {
            Nodes =
            [
                definition.Nodes[0] with
                {
                    Component = new AgentOrchestrationComponentReference
                    {
                        ComponentType = "missing.component",
                        Version = "1"
                    }
                },
                definition.Nodes[1]
            ]
        };
        var compiler = new AgentOrchestrationGraphCompiler(CreateMediaRegistry(targetMediaType: "image/*"));

        var unknownResult = compiler.Compile(unknown);
        var staleResult = compiler.Compile(definition with
        {
            Nodes =
            [
                definition.Nodes[0] with
                {
                    Component = definition.Nodes[0].Component with { ContractHash = "sha256:stale" }
                },
                definition.Nodes[1]
            ]
        });

        Assert.IsTrue(unknownResult.Issues.Any(issue => issue.Code == "graph.node_component_unknown"));
        Assert.IsTrue(staleResult.Issues.Any(issue => issue.Code == "graph.node_component_contract_mismatch"));
    }

    [TestMethod]
    public void GraphLayout_IsSerializedSeparatelyFromExecutableDefinition()
    {
        var layout = new AgentOrchestrationGraphLayout
        {
            GraphId = "media-graph",
            BaseRevisionId = "media-graph/r001",
            Viewport = new AgentOrchestrationViewport { X = 12, Y = 24, Zoom = 0.8 },
            Nodes = [new AgentOrchestrationNodeLayout { NodeId = "load", X = 100, Y = 200 }]
        };

        var graphJson = JsonSerializer.Serialize(CreateMediaGraph(), AgentOrchestrationJson.CreateSerializerOptions());
        var layoutJson = JsonSerializer.Serialize(layout, AgentOrchestrationJson.CreateSerializerOptions());

        Assert.IsFalse(graphJson.Contains("viewport", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(layoutJson, "\"viewport\"");
        StringAssert.Contains(layoutJson, "\"nodeId\":\"load\"");
    }

    private static AgentOrchestrationComponentRegistry CreateMediaRegistry(string targetMediaType)
    {
        var builtIn = AgentOrchestrationComponentRegistry.Default;
        return new AgentOrchestrationComponentRegistry(
        [
            .. builtIn.Components.Select(component => component.Descriptor),
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = "test.media.load-image",
                Version = "1",
                DisplayName = "Load image",
                Category = "media",
                NodeKind = AgentOrchestrationNodeKind.Tool,
                ExecutorId = "test.media.load-image/v1",
                InputPorts =
                [
                    Port(
                        "source",
                        AgentOrchestrationDataTypes.Artifact,
                        AgentOrchestrationPortCardinality.One,
                        ["image/*"],
                        AgentOrchestrationValueDelivery.Artifact,
                        required: true)
                ],
                OutputPorts =
                [
                    Port(
                        "image",
                        AgentOrchestrationDataTypes.Artifact,
                        AgentOrchestrationPortCardinality.One,
                        ["image/png"],
                        AgentOrchestrationValueDelivery.Artifact)
                ]
            },
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = "test.media.thumbnail",
                Version = "1",
                DisplayName = "Create thumbnail",
                Category = "media",
                NodeKind = AgentOrchestrationNodeKind.Tool,
                ExecutorId = "test.media.thumbnail/v1",
                InputPorts =
                [
                    Port(
                        "image",
                        AgentOrchestrationDataTypes.Artifact,
                        AgentOrchestrationPortCardinality.One,
                        [targetMediaType],
                        AgentOrchestrationValueDelivery.Artifact,
                        required: true)
                ],
                OutputPorts =
                [
                    Port(
                        "thumbnail",
                        AgentOrchestrationDataTypes.Artifact,
                        AgentOrchestrationPortCardinality.One,
                        ["image/png"],
                        AgentOrchestrationValueDelivery.Artifact)
                ]
            }
        ], builtIn.Triggers.Select(trigger => trigger.Descriptor));
    }

    private static AgentOrchestrationGraphDefinition CreateMediaGraph()
        => new()
        {
            GraphId = "media-graph",
            RevisionId = "media-graph/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Create a thumbnail from an uploaded image.",
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = "source-image",
                    Contract = new AgentOrchestrationDataContract
                    {
                        DataType = AgentOrchestrationDataTypes.Artifact,
                        MediaTypes = ["image/png"],
                        Deliveries = [AgentOrchestrationValueDelivery.Artifact]
                    },
                    DefaultValue = new AgentOrchestrationValueEnvelope
                    {
                        DataType = AgentOrchestrationDataTypes.Artifact,
                        Artifacts =
                        [
                            new AgentOrchestrationArtifactReference
                            {
                                ArtifactId = "artifact-image-001",
                                ContentType = "image/png",
                                FileName = "source.png",
                                SizeBytes = 1024,
                                Sha256 = new string('a', 64)
                            }
                        ]
                    }
                }
            ],
            Triggers =
            [
                new AgentOrchestrationTriggerDefinition
                {
                    TriggerId = "manual",
                    Trigger = new AgentOrchestrationTriggerReference
                    {
                        TriggerType = AgentOrchestrationTriggerTypes.Manual,
                        Version = "1"
                    }
                }
            ],
            Nodes =
            [
                ToolNode("load", "test.media.load-image", "load_image") with
                {
                    GraphInputBindings =
                    [
                        new AgentOrchestrationGraphInputBinding
                        {
                            InputId = "source-image",
                            TargetPortId = "source"
                        }
                    ]
                },
                ToolNode("thumbnail", "test.media.thumbnail", "create_thumbnail")
            ],
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "load-to-thumbnail",
                    FromNodeId = "load",
                    ToNodeId = "thumbnail",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "image",
                            TargetPortId = "image",
                            Aggregation = AgentOrchestrationDataAggregation.Replace
                        }
                    ]
                }
            ]
        };

    private static AgentOrchestrationNodeDefinition ToolNode(string nodeId, string componentType, string toolId)
        => new()
        {
            NodeId = nodeId,
            Kind = AgentOrchestrationNodeKind.Tool,
            Title = nodeId,
            Objective = $"Execute {nodeId}.",
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
            ExpectedOutputContract = "artifact"
        };

    private static AgentOrchestrationPortDefinition Port(
        string portId,
        string dataType,
        AgentOrchestrationPortCardinality cardinality,
        IReadOnlyList<string> mediaTypes,
        AgentOrchestrationValueDelivery delivery,
        bool required = false)
        => new()
        {
            PortId = portId,
            DisplayName = portId,
            Required = required,
            Contract = new AgentOrchestrationDataContract
            {
                DataType = dataType,
                Cardinality = cardinality,
                MediaTypes = mediaTypes,
                Deliveries = [delivery]
            }
        };

    private static string FormatIssues(AgentOrchestrationCompilationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
}
