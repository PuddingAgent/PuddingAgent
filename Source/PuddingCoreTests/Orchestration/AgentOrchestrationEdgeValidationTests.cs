using System.Text.Json;
using PuddingCode.Orchestration;

namespace PuddingCoreTests.Orchestration;

/// <summary>
/// S2-B1 edge contract and validation matrix tests (doc 83 §12.3, doc 85 §6.1).
/// Covers edge predicate contract, sourcePath validation, and the compiler accept/reject matrix.
/// </summary>
[TestClass]
public sealed class AgentOrchestrationEdgeValidationTests
{
    private readonly AgentOrchestrationGraphCompiler _compiler = new();

    // ---- Edge predicate (doc 83 §12.3) ----

    [TestMethod]
    public void Compile_AcceptsControlEdgeWithValidPredicate()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("research", "provider/model-a"),
            CreateSubAgentNode("design", "provider/model-b")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "research-to-design",
                    FromNodeId = "research",
                    ToNodeId = "design",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = "$"
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
        Assert.IsNotNull(result.Definition!.Edges[0].Predicate);
        Assert.AreEqual("pudding.predicate.branch/v1", result.Definition.Edges[0].Predicate!.EvaluatorId);
    }

    [TestMethod]
    public void Compile_AcceptsControlEdgeWithoutPredicate()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
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
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
        Assert.IsNull(result.Definition!.Edges[0].Predicate);
    }

    [TestMethod]
    public void Compile_RejectsPredicateOnDataEdge()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            TargetPortId = "context"
                        }
                    ],
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = "$"
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_control_only"));
    }

    [TestMethod]
    public void Compile_RejectsPredicateWithMissingEvaluatorAndVersion()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "",
                        Version = "",
                        SourcePortId = "result"
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_evaluator_required"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_version_required"));
    }

    [TestMethod]
    public void Compile_RejectsPredicateWithMissingSourcePortId()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = ""
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_source_port_required"));
    }

    [TestMethod]
    public void Compile_RejectsPredicateWithUnknownSourcePort()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "nonexistent_port",
                        SourcePath = "$"
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_source_port_unknown"));
    }

    [TestMethod]
    public void Compile_RejectsPredicateWithInvalidSourcePath()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = "$eval(payload)"
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_source_path_invalid"));
    }

    [TestMethod]
    public void Compile_AcceptsPredicateWithValidSourcePaths()
    {
        var paths = new[] { "$", "$.field", "$.field.subfield", "$.field[0]", "$.field[0].nested" };
        foreach (var sourcePath in paths)
        {
            var definition = CreateDefinition(
            [
                CreateSubAgentNode("a", "provider/model"),
                CreateSubAgentNode("b", "provider/model")
            ]) with
            {
                Edges =
                [
                    new AgentOrchestrationEdgeDefinition
                    {
                        EdgeId = "a-to-b",
                        FromNodeId = "a",
                        ToNodeId = "b",
                        Kind = AgentOrchestrationEdgeKind.Control,
                        Predicate = new AgentOrchestrationEdgePredicate
                        {
                            EvaluatorId = "pudding.predicate.branch/v1",
                            Version = "1",
                            SourcePortId = "result",
                            SourcePath = sourcePath
                        }
                    }
                ]
            };

            var result = _compiler.Compile(definition);
            Assert.IsTrue(result.Success, $"Predicate SourcePath '{sourcePath}' should be valid. {FormatIssues(result)}");
        }
    }

    [TestMethod]
    public void Compile_RejectsPredicateWithInvalidSourcePaths()
    {
        var paths = new[] { "$..field", "$.eval(script)", "field.subfield" };
        foreach (var sourcePath in paths)
        {
            var definition = CreateDefinition(
            [
                CreateSubAgentNode("a", "provider/model"),
                CreateSubAgentNode("b", "provider/model")
            ]) with
            {
                Edges =
                [
                    new AgentOrchestrationEdgeDefinition
                    {
                        EdgeId = "a-to-b",
                        FromNodeId = "a",
                        ToNodeId = "b",
                        Kind = AgentOrchestrationEdgeKind.Control,
                        Predicate = new AgentOrchestrationEdgePredicate
                        {
                            EvaluatorId = "pudding.predicate.branch/v1",
                            Version = "1",
                            SourcePortId = "result",
                            SourcePath = sourcePath
                        }
                    }
                ]
            };

            var result = _compiler.Compile(definition);
            Assert.IsFalse(result.Success, $"Predicate SourcePath '{sourcePath}' should be invalid. {FormatIssues(result)}");
            Assert.IsTrue(
                result.Issues.Any(issue => issue.Code == "graph.edge_predicate_source_path_invalid"),
                $"Predicate SourcePath '{sourcePath}' should produce graph.edge_predicate_source_path_invalid.");
        }
    }

    // ---- SourcePath validation (doc 85 §6.1 sourcePath dimension) ----

    [TestMethod]
    public void Compile_AcceptsValidSourcePaths()
    {
        var paths = new[] { "$", "$.field", "$.field.subfield", "$.field[0]", "$.field[0].nested" };
        foreach (var sourcePath in paths)
        {
            var definition = CreateDefinition(
            [
                CreateSubAgentNode("a", "provider/model"),
                CreateSubAgentNode("b", "provider/model")
            ]) with
            {
                Edges =
                [
                    new AgentOrchestrationEdgeDefinition
                    {
                        EdgeId = "a-to-b",
                        FromNodeId = "a",
                        ToNodeId = "b",
                        Kind = AgentOrchestrationEdgeKind.Data,
                        Bindings =
                        [
                            new AgentOrchestrationDataBinding
                            {
                                SourcePortId = "result",
                                SourcePath = sourcePath,
                                TargetPortId = "context"
                            }
                        ]
                    }
                ]
            };

            var result = _compiler.Compile(definition);
            Assert.IsTrue(result.Success, $"SourcePath '{sourcePath}' should be valid. {FormatIssues(result)}");
        }
    }

    [TestMethod]
    public void Compile_RejectsRecursiveDescentSourcePath()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            SourcePath = "$..field",
                            TargetPortId = "context"
                        }
                    ]
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_source_path_invalid"));
    }

    [TestMethod]
    public void Compile_RejectsFunctionCallSourcePath()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            SourcePath = "$.eval(script)",
                            TargetPortId = "context"
                        }
                    ]
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_source_path_invalid"));
    }

    [TestMethod]
    public void Compile_RejectsNonRootAnchoredSourcePath()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            SourcePath = "field.subfield",
                            TargetPortId = "context"
                        }
                    ]
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_source_path_invalid"));
    }

    // ---- Edge immutability through snapshot ----

    [TestMethod]
    public void Compile_EdgeSnapshotIsImmutable()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("research", "provider/model"),
            CreateSubAgentNode("design", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "research-to-design",
                    FromNodeId = "research",
                    ToNodeId = "design",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = "$.score",
                        Parameters = new Dictionary<string, JsonElement>
                        {
                            ["threshold"] = JsonSerializer.SerializeToElement(0.8)
                        }
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
        var compiledEdge = result.Definition!.Edges[0];
        Assert.AreEqual("pudding.predicate.branch/v1", compiledEdge.Predicate!.EvaluatorId);
        Assert.AreEqual("$.score", compiledEdge.Predicate.SourcePath);
        Assert.IsTrue(compiledEdge.Predicate.Parameters.ContainsKey("threshold"));
    }

    [TestMethod]
    public void Compile_EdgePredicate_ContractHashWhitespaceNormalizesToNull()
    {
        foreach (var contractHash in new[] { "", "   ", "\t" })
        {
            var definition = CreateDefinition(
            [
                CreateSubAgentNode("a", "provider/model"),
                CreateSubAgentNode("b", "provider/model")
            ]) with
            {
                Edges =
                [
                    new AgentOrchestrationEdgeDefinition
                    {
                        EdgeId = "a-to-b",
                        FromNodeId = "a",
                        ToNodeId = "b",
                        Kind = AgentOrchestrationEdgeKind.Control,
                        Predicate = new AgentOrchestrationEdgePredicate
                        {
                            EvaluatorId = "pudding.predicate.branch/v1",
                            Version = "1",
                            ContractHash = contractHash,
                            SourcePortId = "result",
                            SourcePath = "$"
                        }
                    }
                ]
            };

            var result = _compiler.Compile(definition);
            Assert.IsTrue(result.Success, FormatIssues(result));
            Assert.IsNull(
                result.Definition!.Edges[0].Predicate!.ContractHash,
                $"ContractHash '{contractHash}' should normalize to null.");
        }

        // A real hash survives snapshot normalization unchanged.
        var hashedDefinition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        ContractHash = "sha256:abc123",
                        SourcePortId = "result",
                        SourcePath = "$"
                    }
                }
            ]
        };

        var hashedResult = _compiler.Compile(hashedDefinition);
        Assert.IsTrue(hashedResult.Success, FormatIssues(hashedResult));
        Assert.AreEqual("sha256:abc123", hashedResult.Definition!.Edges[0].Predicate!.ContractHash);
    }

    // ---- Graph input binding sourcePath is NOT validated (graph input binding has no SourcePath field) ----
    // ---- Trigger input binding sourcePath IS validated (doc 85 §6.1) ----

    [TestMethod]
    public void Compile_RejectsTriggerInputBindingWithInvalidSourcePath()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("worker", "provider/model")
        ]) with
        {
            Triggers =
            [
                new AgentOrchestrationTriggerDefinition
                {
                    TriggerId = "manual-trigger",
                    Trigger = new AgentOrchestrationTriggerReference
                    {
                        TriggerType = AgentOrchestrationTriggerTypes.Manual,
                        Version = "1"
                    },
                    InputBindings =
                    [
                        new AgentOrchestrationTriggerInputBinding
                        {
                            SourcePath = "$$invalid",
                            TargetInputId = "request"
                        }
                    ]
                }
            ]
        };

        // Trigger input binding SourcePath validation is out of scope for B1 edge contract; the
        // compiler already checks non-empty via RequireText. This test confirms no edge-specific
        // path validation leaks into trigger validation.
        var result = _compiler.Compile(definition);
        Assert.IsTrue(result.Success, FormatIssues(result));
    }

    // ---- Compiler matrix (doc 85 §6.1): dataType / MIME / cardinality / delivery ----
    // Each dimension is exercised through a data edge between two custom components whose port
    // contracts isolate the dimension under test.

    [TestMethod]
    public void Compile_DataTypeMatrix_AcceptsTextToTextAndAnythingToAnyTarget()
    {
        var text = Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]);
        var any = Contract(AgentOrchestrationDataTypes.Any);

        var textToText = CreatePortMatrixCompiler(text, text)
            .Compile(CreatePortMatrixGraph(CreateDataEdge()));
        Assert.IsTrue(textToText.Success, FormatIssues(textToText));

        var anythingToAny = CreatePortMatrixCompiler(text, any)
            .Compile(CreatePortMatrixGraph(CreateDataEdge()));
        Assert.IsTrue(anythingToAny.Success, FormatIssues(anythingToAny));
    }

    [TestMethod]
    public void Compile_DataTypeMatrix_RejectsSemanticImageToText()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract("pudding.image", ["image/png"]),
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_ports_incompatible"));
    }

    [TestMethod]
    public void Compile_MimeMatrix_AcceptsImagePngIntoImageWildcard()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract(AgentOrchestrationDataTypes.Artifact, ["image/png"],
                deliveries: AgentOrchestrationValueDelivery.Artifact),
            Contract(AgentOrchestrationDataTypes.Artifact, ["image/*"],
                deliveries: AgentOrchestrationValueDelivery.Artifact));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsTrue(result.Success, FormatIssues(result));
    }

    [TestMethod]
    public void Compile_MimeMatrix_RejectsAudioMpegIntoImageWildcard()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract(AgentOrchestrationDataTypes.Artifact, ["audio/mpeg"],
                deliveries: AgentOrchestrationValueDelivery.Artifact),
            Contract(AgentOrchestrationDataTypes.Artifact, ["image/*"],
                deliveries: AgentOrchestrationValueDelivery.Artifact));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_ports_incompatible"));
    }

    [TestMethod]
    public void Compile_CardinalityMatrix_AcceptsOneToOneOneToManyAndManyToMany()
    {
        var one = Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]);
        var many = Contract(
            AgentOrchestrationDataTypes.Text,
            ["text/plain"],
            AgentOrchestrationPortCardinality.Many);

        var oneToOne = CreatePortMatrixCompiler(one, one)
            .Compile(CreatePortMatrixGraph(CreateDataEdge()));
        Assert.IsTrue(oneToOne.Success, FormatIssues(oneToOne));

        var oneToMany = CreatePortMatrixCompiler(one, many)
            .Compile(CreatePortMatrixGraph(CreateDataEdge()));
        Assert.IsTrue(oneToMany.Success, FormatIssues(oneToMany));

        var manyToMany = CreatePortMatrixCompiler(many, many)
            .Compile(CreatePortMatrixGraph(CreateDataEdge()));
        Assert.IsTrue(manyToMany.Success, FormatIssues(manyToMany));
    }

    [TestMethod]
    public void Compile_CardinalityMatrix_RejectsManyToOne()
    {
        var one = Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]);
        var many = Contract(
            AgentOrchestrationDataTypes.Text,
            ["text/plain"],
            AgentOrchestrationPortCardinality.Many);

        var result = CreatePortMatrixCompiler(many, one)
            .Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_ports_incompatible"));
    }

    [TestMethod]
    public void Compile_DeliveryMatrix_AcceptsIntersectingDeliveryModes()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract("pudding.payload",
                deliveries: [AgentOrchestrationValueDelivery.Inline, AgentOrchestrationValueDelivery.Artifact]),
            Contract("pudding.payload",
                deliveries: [AgentOrchestrationValueDelivery.Artifact, AgentOrchestrationValueDelivery.Stream]));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsTrue(result.Success, FormatIssues(result));
    }

    [TestMethod]
    public void Compile_DeliveryMatrix_RejectsSourceArtifactIntoTargetInlineOnly()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract("pudding.payload", deliveries: AgentOrchestrationValueDelivery.Artifact),
            Contract("pudding.payload", deliveries: AgentOrchestrationValueDelivery.Inline));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_ports_incompatible"));
    }

    // ---- Compiler matrix (doc 85 §6.1): binding / graph input / topology ----

    [TestMethod]
    public void Compile_BindingMatrix_AcceptsSingleDeterministicSource()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]),
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge()));

        Assert.IsTrue(result.Success, FormatIssues(result));
        Assert.IsNotNull(result.Definition!.Edges[0].Bindings[0]);
    }

    [TestMethod]
    public void Compile_BindingMatrix_RejectsTwoSourcesReplaceToSingleValue()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]),
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));
        var definition = CreateMatrixDefinition(
        [
            CreateMatrixNode("test.edge.source", "src-1"),
            CreateMatrixNode("test.edge.source", "src-2"),
            CreateMatrixNode("test.edge.target", "tgt")
        ],
        [
            CreateDataEdge("e1", "src-1", "tgt"),
            CreateDataEdge("e2", "src-2", "tgt")
        ]);

        var result = compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_single_input_multiple_bindings"));
    }

    [TestMethod]
    public void Compile_BindingMatrix_RejectsAppendAggregationOnSingleValuePort()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]),
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));

        var result = compiler.Compile(CreatePortMatrixGraph(
            CreateDataEdge(aggregation: AgentOrchestrationDataAggregation.Append)));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_single_port_requires_replace"));
    }

    [TestMethod]
    public void Compile_GraphInputMatrix_AcceptsCompatibleContract()
    {
        var compiler = CreateGraphInputCompiler(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));
        var definition = CreateGraphInputGraph(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]),
            bind: true);

        var result = compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
    }

    [TestMethod]
    public void Compile_GraphInputMatrix_RejectsIncompatibleContract()
    {
        var compiler = CreateGraphInputCompiler(
            Contract(AgentOrchestrationDataTypes.Json, ["application/json"]));
        var definition = CreateGraphInputGraph(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]),
            bind: true);

        var result = compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_input_port_incompatible"));
    }

    [TestMethod]
    public void Compile_GraphInputMatrix_RejectsUnboundRequiredInput()
    {
        var compiler = CreateGraphInputCompiler(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));
        var definition = CreateGraphInputGraph(
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]),
            bind: false);

        var result = compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_required_input_unbound"));
    }

    [TestMethod]
    public void Compile_TopologyMatrix_AcceptsDag()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
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
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
        CollectionAssert.AreEqual(new[] { "a", "b" }, result.TopologicalNodeIds.ToArray());
    }

    [TestMethod]
    public void Compile_TopologyMatrix_RejectsSelfLoop()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-a",
                    FromNodeId = "a",
                    ToNodeId = "a",
                    Kind = AgentOrchestrationEdgeKind.Control
                }
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_self_reference"));
    }

    [TestMethod]
    public void Compile_TopologyMatrix_RejectsPureDataCycle()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                CreateSubAgentDataEdge("a-to-b", "a", "b"),
                CreateSubAgentDataEdge("b-to-a", "b", "a")
            ]
        };

        var result = _compiler.Compile(definition);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.cycle_detected"));
    }

    // ---- Diagnostic projection fields on core issues (doc 83 §8) ----

    [TestMethod]
    public void Compile_IncompatibleBindingIssue_CarriesProjectionFields()
    {
        var compiler = CreatePortMatrixCompiler(
            Contract("pudding.image", ["image/png"]),
            Contract(AgentOrchestrationDataTypes.Text, ["text/plain"]));

        var result = compiler.Compile(CreatePortMatrixGraph(CreateDataEdge("edge-img-to-text")));

        var issue = result.Issues.First(item => item.Code == "graph.data_ports_incompatible");
        Assert.AreEqual("error", issue.Severity);
        Assert.AreEqual("edge", issue.ElementType);
        Assert.AreEqual("edge-img-to-text", issue.ElementId);
        Assert.AreEqual("in", issue.PortId);
    }

    [TestMethod]
    public void Compile_PredicateSourcePathIssue_CarriesProjectionFields()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = "$eval(payload)"
                    }
                }
            ]
        };

        var result = _compiler.Compile(definition);

        var issue = result.Issues.First(item => item.Code == "graph.edge_predicate_source_path_invalid");
        Assert.AreEqual("error", issue.Severity);
        Assert.AreEqual("edge", issue.ElementType);
        Assert.AreEqual("a-to-b", issue.ElementId);
        Assert.AreEqual("result", issue.PortId);
    }

    [TestMethod]
    public void Compile_PredicateWithNullSourcePath_NormalizesToDefaultRoot()
    {
        var definition = CreateDefinition(
        [
            CreateSubAgentNode("a", "provider/model"),
            CreateSubAgentNode("b", "provider/model")
        ]) with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "a-to-b",
                    FromNodeId = "a",
                    ToNodeId = "b",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = null!
                    }
                }
            ]
        };

        // A null SourcePath (e.g. JSON "sourcePath": null) must not throw in the snapshot;
        // it falls back to the documented root default "$" (AgentOrchestrationEdgePredicate.SourcePath).
        var result = _compiler.Compile(definition);

        Assert.IsTrue(result.Success, FormatIssues(result));
        Assert.AreEqual("$", result.Definition!.Edges[0].Predicate!.SourcePath);
    }

    private static AgentOrchestrationGraphDefinition CreateDefinition(
        IReadOnlyList<AgentOrchestrationNodeDefinition> nodes)
        => new()
        {
            GraphId = "graph-edge-001",
            RevisionId = "graph-edge-001/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Validate edge definitions.",
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

    private static string FormatIssues(AgentOrchestrationCompilationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));

    private static AgentOrchestrationDataContract Contract(
        string dataType,
        IReadOnlyList<string>? mediaTypes = null,
        AgentOrchestrationPortCardinality cardinality = AgentOrchestrationPortCardinality.One,
        params AgentOrchestrationValueDelivery[] deliveries)
        => new()
        {
            DataType = dataType,
            MediaTypes = mediaTypes ?? Array.Empty<string>(),
            Cardinality = cardinality,
            Deliveries = deliveries.Length > 0
                ? deliveries
                : [AgentOrchestrationValueDelivery.Inline]
        };

    private static AgentOrchestrationPortDefinition Port(
        string portId,
        AgentOrchestrationDataContract contract)
        => new()
        {
            PortId = portId,
            DisplayName = portId,
            Contract = contract
        };

    /// <summary>
    /// Builds a compiler over two custom sub-agent components whose only ports isolate the
    /// dimension under test: source exposes output port 'out', target accepts input port 'in'.
    /// </summary>
    private static AgentOrchestrationGraphCompiler CreatePortMatrixCompiler(
        AgentOrchestrationDataContract sourceContract,
        AgentOrchestrationDataContract targetContract)
    {
        var components = new[]
        {
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = "test.edge.source",
                Version = "1",
                DisplayName = "Matrix source",
                Category = "test",
                NodeKind = AgentOrchestrationNodeKind.SubAgent,
                ExecutorId = "test.edge.source/v1",
                OutputPorts = [Port("out", sourceContract)]
            },
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = "test.edge.target",
                Version = "1",
                DisplayName = "Matrix target",
                Category = "test",
                NodeKind = AgentOrchestrationNodeKind.SubAgent,
                ExecutorId = "test.edge.target/v1",
                InputPorts = [Port("in", targetContract)]
            }
        };
        return new AgentOrchestrationGraphCompiler(new AgentOrchestrationComponentRegistry(components));
    }

    /// <summary>
    /// Builds a compiler over a single custom sub-agent component whose input port 'in' is
    /// required, used by the graph-input matrix accept/reject cases.
    /// </summary>
    private static AgentOrchestrationGraphCompiler CreateGraphInputCompiler(
        AgentOrchestrationDataContract inputContract)
    {
        var components = new[]
        {
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = "test.edge.target",
                Version = "1",
                DisplayName = "Matrix target",
                Category = "test",
                NodeKind = AgentOrchestrationNodeKind.SubAgent,
                ExecutorId = "test.edge.target/v1",
                InputPorts =
                [
                    new AgentOrchestrationPortDefinition
                    {
                        PortId = "in",
                        DisplayName = "in",
                        Contract = inputContract,
                        Required = true
                    }
                ]
            }
        };
        return new AgentOrchestrationGraphCompiler(new AgentOrchestrationComponentRegistry(components));
    }

    private static AgentOrchestrationGraphDefinition CreateMatrixDefinition(
        IReadOnlyList<AgentOrchestrationNodeDefinition> nodes,
        IReadOnlyList<AgentOrchestrationEdgeDefinition> edges)
        => new()
        {
            GraphId = "matrix-graph",
            RevisionId = "matrix-graph/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Matrix edge validation.",
            MaxConcurrency = 2,
            Nodes = nodes,
            Edges = edges
        };

    private static AgentOrchestrationGraphDefinition CreatePortMatrixGraph(AgentOrchestrationEdgeDefinition edge)
        => CreateMatrixDefinition(
        [
            CreateMatrixNode("test.edge.source", "src"),
            CreateMatrixNode("test.edge.target", "tgt")
        ],
        [edge]);

    private static AgentOrchestrationGraphDefinition CreateGraphInputGraph(
        AgentOrchestrationDataContract graphInputContract,
        bool bind)
        => new()
        {
            GraphId = "matrix-graph",
            RevisionId = "matrix-graph/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Matrix graph input validation.",
            MaxConcurrency = 1,
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = "gi",
                    Contract = graphInputContract
                }
            ],
            Nodes =
            [
                CreateMatrixNode("test.edge.target", "worker") with
                {
                    GraphInputBindings = bind
                        ? [new AgentOrchestrationGraphInputBinding { InputId = "gi", TargetPortId = "in" }]
                        : []
                }
            ]
        };

    private static AgentOrchestrationEdgeDefinition CreateDataEdge(
        string edgeId = "src-to-tgt",
        string fromNodeId = "src",
        string toNodeId = "tgt",
        AgentOrchestrationDataAggregation aggregation = AgentOrchestrationDataAggregation.Replace)
        => new()
        {
            EdgeId = edgeId,
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Kind = AgentOrchestrationEdgeKind.Data,
            Bindings =
            [
                new AgentOrchestrationDataBinding
                {
                    SourcePortId = "out",
                    SourcePath = "$",
                    TargetPortId = "in",
                    Aggregation = aggregation
                }
            ]
        };

    private static AgentOrchestrationEdgeDefinition CreateSubAgentDataEdge(
        string edgeId,
        string fromNodeId,
        string toNodeId)
        => new()
        {
            EdgeId = edgeId,
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Kind = AgentOrchestrationEdgeKind.Data,
            Bindings =
            [
                new AgentOrchestrationDataBinding
                {
                    SourcePortId = "result",
                    SourcePath = "$",
                    TargetPortId = "context"
                }
            ]
        };

    private static AgentOrchestrationNodeDefinition CreateMatrixNode(string componentType, string nodeId)
        => new()
        {
            NodeId = nodeId,
            Kind = AgentOrchestrationNodeKind.SubAgent,
            Title = nodeId,
            Objective = $"Execute {nodeId}.",
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = componentType,
                Version = "1"
            },
            Executor = new AgentOrchestrationExecutorBinding
            {
                Kind = AgentOrchestrationExecutorKind.SubAgent,
                Role = "specialist",
                TemplateId = "specialist",
                RouteKey = "provider/model"
            },
            ExpectedOutputContract = AgentOrchestrationDataTypes.Content
        };
}