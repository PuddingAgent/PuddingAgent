using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Orchestration;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Services.Orchestration;

/// <summary>
/// S2-B1 edge-dimension authoring tests (doc 85 §5.1/§6.1, isomorphic to
/// AgentOrchestrationAuthoringServiceTests lines 126-333). Each test focuses on edge contract
/// changes: immutability, concurrency, forged audit fields, invalid edge 422+diagnostics,
/// and zero side effects.
/// </summary>
[TestClass]
public sealed class AgentOrchestrationEdgeAuthoringTests
{
    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private MutableTimeProvider _timeProvider = null!;
    private RecordingSignal _signal = null!;
    private SqliteAgentOrchestrationStore _store = null!;
    private AgentOrchestrationAuthoringService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "orchestration-edge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var databasePath = Path.Combine(_testRoot, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);

        _timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        _signal = new RecordingSignal();
        _store = CreateStore();
        _service = new AgentOrchestrationAuthoringService(
            _store,
            new AgentOrchestrationGraphCompiler(),
            _timeProvider);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public async Task CreateRevision_EdgeChangeProducesNewRevision_OldRevisionByteIdentical()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());
        var before = (await _store.GetRevisionAsync("graph-edge/r001"))!;
        var beforeJson = JsonSerializer.Serialize(before, AgentOrchestrationJson.CreateSerializerOptions());
        var beforeHash = ComputeSha256(beforeJson);

        // Edit: add a second data edge.
        var draft = CreateDefinitionWithPredicate() with
        {
            Objective = "Revised: added data edge",
            Edges =
            [
                CreateControlEdgeWithPredicate(),
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "root-to-child-data",
                    FromNodeId = "root",
                    ToNodeId = "child",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            TargetPortId = "context"
                        }
                    ]
                }
            ],
            // Client-forged audit fields — must NOT be trusted.
            RevisionId = "graph-edge/r999",
            Revision = 999,
            ParentRevisionId = "graph-edge/r777",
            CreatedByAgentId = "evil-client",
            CreatedAtUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, result.Status);
        var saved = result.Value!;
        Assert.AreEqual("graph-edge/r002", saved.RevisionId);
        Assert.AreEqual(2, saved.Revision);
        Assert.AreEqual("graph-edge/r001", saved.ParentRevisionId);
        Assert.AreEqual("admin-user", saved.CreatedByAgentId);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero), saved.CreatedAtUtc);

        // Old revision remains byte-identical.
        var after = (await _store.GetRevisionAsync("graph-edge/r001"))!;
        var afterJson = JsonSerializer.Serialize(after, AgentOrchestrationJson.CreateSerializerOptions());
        Assert.AreEqual(beforeJson, afterJson);
        Assert.AreEqual(beforeHash, ComputeSha256(afterJson));
        Assert.AreEqual("Original: control edge with predicate.", after.Objective);
    }

    [TestMethod]
    public async Task ConcurrentEdgeChange_OnlyOneRevisionIsApplied()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        var results = await Task.WhenAll(
            _service.CreateRevisionAsync(new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinitionWithPredicate() with { Objective = "Concurrent edge edit A" }
            }, "admin-a", default),
            _service.CreateRevisionAsync(new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinitionWithPredicate() with { Objective = "Concurrent edge edit B" }
            }, "admin-b", default));

        Assert.HasCount(1, results.Where(r => r.Status == AgentOrchestrationStoreStatus.Applied));
        Assert.HasCount(1, results.Where(r => r.Status == AgentOrchestrationStoreStatus.Conflict));
        var revisions = await _store.ListRevisionsAsync("graph-edge", 10);
        Assert.AreEqual(2, revisions.Count);
    }

    [TestMethod]
    public async Task CreateRevision_ForgedEdgeAuditFieldsNotTrusted()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        var draft = CreateDefinitionWithPredicate() with
        {
            Objective = "Second revision with edge predicate",
            RevisionId = "graph-edge/r-forged",
            Revision = 4242,
            ParentRevisionId = "graph-edge/r-forged-parent",
            CreatedByAgentId = "attacker",
            CreatedAtUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "root-to-child",
                    FromNodeId = "root",
                    ToNodeId = "child",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.forged/v99",
                        Version = "99",
                        SourcePortId = "result",
                        SourcePath = "$"
                    }
                }
            ]
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, result.Status);
        var saved = result.Value!;
        Assert.AreEqual("graph-edge/r002", saved.RevisionId);
        Assert.AreEqual(2, saved.Revision);
        Assert.AreEqual("graph-edge/r001", saved.ParentRevisionId);
        Assert.AreEqual("admin-user", saved.CreatedByAgentId);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero), saved.CreatedAtUtc);
        // The edge predicate content IS preserved (server only authors audit fields, not content).
        Assert.IsNotNull(saved.Edges[0].Predicate);
        Assert.AreEqual("pudding.predicate.forged/v99", saved.Edges[0].Predicate!.EvaluatorId);
    }

    [TestMethod]
    public async Task CreateRevision_InvalidEdgePredicate_Returns422WithDiagnostics()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        // Data edge with a predicate — must be rejected with 422.
        var draft = CreateDefinitionWithPredicate() with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "root-to-child",
                    FromNodeId = "root",
                    ToNodeId = "child",
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

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.AreEqual("orchestration.definition_invalid", result.ErrorCode);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.edge_predicate_control_only"));
        Assert.IsNull(await _store.GetRevisionAsync("graph-edge/r002"));
    }

    [TestMethod]
    public async Task CreateRevision_InvalidEdgeSourcePath_Returns422WithDiagnostics()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        var draft = CreateDefinitionWithPredicate() with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "root-to-child",
                    FromNodeId = "root",
                    ToNodeId = "child",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "result",
                            SourcePath = "$eval(malicious)",
                            TargetPortId = "context"
                        }
                    ]
                }
            ]
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.AreEqual("orchestration.definition_invalid", result.ErrorCode);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.data_source_path_invalid"));
        Assert.IsNull(await _store.GetRevisionAsync("graph-edge/r002"));
    }

    [TestMethod]
    public async Task CreateRevision_EdgePortIncompatible_Returns422WithDiagnostics()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        // Gate decision (JSON) into human-input prompt (content) is a dataType mismatch; the
        // 422 diagnostics must carry the edge element id and target port id (doc 83 §8).
        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = CreateIncompatibleEdgeDraft()
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.AreEqual("orchestration.definition_invalid", result.ErrorCode);
        var issue = result.Issues.First(item => item.Code == "graph.data_ports_incompatible");
        Assert.AreEqual("error", issue.Severity);
        Assert.AreEqual("edge", issue.ElementType);
        Assert.AreEqual("gate-to-ask", issue.ElementId);
        Assert.AreEqual("prompt", issue.PortId);
        Assert.IsNull(await _store.GetRevisionAsync("graph-edge/r002"));
    }

    [TestMethod]
    public async Task CreateRevision_EdgeChange_DoesNotCreateRunsOrEvents()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinitionWithPredicate() with
                {
                    Objective = "Edge-only edit, no run facts",
                    Edges =
                    [
                        CreateControlEdgeWithPredicate(),
                        new AgentOrchestrationEdgeDefinition
                        {
                            EdgeId = "root-to-child-data",
                            FromNodeId = "root",
                            ToNodeId = "child",
                            Kind = AgentOrchestrationEdgeKind.Data,
                            Bindings =
                            [
                                new AgentOrchestrationDataBinding
                                {
                                    SourcePortId = "result",
                                    TargetPortId = "context"
                                }
                            ]
                        }
                    ]
                }
            },
            "admin-user",
            default);

        var runs = await _store.ListRunsAsync(null, "graph-edge", null, 10, 0);
        var events = await _store.GetEventsAfterAsync("graph-edge", 0, 10);

        Assert.AreEqual(0, runs.Count);
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task CreateRevision_EdgeWithPredicate_RoundTripsThroughStore()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinitionWithPredicate() with
                {
                    Objective = "Predicate round-trip",
                    Edges =
                    [
                        new AgentOrchestrationEdgeDefinition
                        {
                            EdgeId = "root-to-child",
                            FromNodeId = "root",
                            ToNodeId = "child",
                            Kind = AgentOrchestrationEdgeKind.Control,
                            Predicate = new AgentOrchestrationEdgePredicate
                            {
                                EvaluatorId = "pudding.predicate.branch/v1",
                                Version = "1",
                                ContractHash = "sha256:abc123",
                                SourcePortId = "result",
                                SourcePath = "$.decision",
                                Parameters = new Dictionary<string, JsonElement>
                                {
                                    ["threshold"] = JsonSerializer.SerializeToElement(0.9)
                                }
                            }
                        }
                    ]
                }
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, result.Status);
        var saved = result.Value!;

        // Reload from store to verify full round-trip.
        var reloaded = await _store.GetRevisionAsync(saved.RevisionId);
        Assert.IsNotNull(reloaded);
        Assert.IsNotNull(reloaded.Edges[0].Predicate);
        Assert.AreEqual("pudding.predicate.branch/v1", reloaded.Edges[0].Predicate!.EvaluatorId);
        Assert.AreEqual("1", reloaded.Edges[0].Predicate.Version);
        Assert.AreEqual("sha256:abc123", reloaded.Edges[0].Predicate.ContractHash);
        Assert.AreEqual("$.decision", reloaded.Edges[0].Predicate.SourcePath);
        Assert.IsTrue(reloaded.Edges[0].Predicate.Parameters.ContainsKey("threshold"));
    }

    [TestMethod]
    public async Task CreateRevision_InvalidPredicateSourcePath_Returns422WithProjectionDiagnostics()
    {
        await SaveDefinitionAsync(CreateDefinitionWithPredicate());

        var draft = CreateDefinitionWithPredicate() with
        {
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "root-to-child",
                    FromNodeId = "root",
                    ToNodeId = "child",
                    Kind = AgentOrchestrationEdgeKind.Control,
                    Predicate = new AgentOrchestrationEdgePredicate
                    {
                        EvaluatorId = "pudding.predicate.branch/v1",
                        Version = "1",
                        SourcePortId = "result",
                        SourcePath = "$..field"
                    }
                }
            ]
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-edge",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.AreEqual("orchestration.definition_invalid", result.ErrorCode);
        var issue = result.Issues.First(item => item.Code == "graph.edge_predicate_source_path_invalid");
        Assert.AreEqual("error", issue.Severity);
        Assert.AreEqual("edge", issue.ElementType);
        Assert.AreEqual("root-to-child", issue.ElementId);
        Assert.AreEqual("result", issue.PortId);
        Assert.IsNull(await _store.GetRevisionAsync("graph-edge/r002"));
    }

    private SqliteAgentOrchestrationStore CreateStore()
        => new(
            _dbFactory,
            new AgentOrchestrationGraphCompiler(),
            _signal,
            _timeProvider,
            NullLogger<SqliteAgentOrchestrationStore>.Instance);

    private async Task SaveDefinitionAsync(AgentOrchestrationGraphDefinition definition)
    {
        var result = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = definition,
            ExpectedCurrentRevision = 0
        });
        Assert.IsTrue(result.Success, result.ErrorMessage);
    }

    private static AgentOrchestrationGraphDefinition CreateDefinitionWithPredicate()
        => new()
        {
            GraphId = "graph-edge",
            RevisionId = "graph-edge/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Original: control edge with predicate.",
            MaxConcurrency = 1,
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
                        InlineValue = JsonSerializer.SerializeToElement("test")
                    }
                }
            ],
            Nodes =
            [
                CreateNode("root"),
                CreateNode("child")
            ],
            Edges =
            [
                CreateControlEdgeWithPredicate()
            ],
            CreatedAtUtc = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero)
        };

    private static AgentOrchestrationEdgeDefinition CreateControlEdgeWithPredicate()
        => new()
        {
            EdgeId = "root-to-child",
            FromNodeId = "root",
            ToNodeId = "child",
            Kind = AgentOrchestrationEdgeKind.Control,
            Predicate = new AgentOrchestrationEdgePredicate
            {
                EvaluatorId = "pudding.predicate.branch/v1",
                Version = "1",
                SourcePortId = "result",
                SourcePath = "$"
            }
        };

    private static AgentOrchestrationNodeDefinition CreateNode(string nodeId)
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
                RouteKey = "deepseek/deepseek-v4-flash"
            },
            GraphInputBindings =
            [
                new AgentOrchestrationGraphInputBinding
                {
                    InputId = "request",
                    TargetPortId = "request"
                }
            ],
            ExpectedOutputContract = "result",
            MaxAttempts = 1
        };

    /// <summary>
    /// Gate decision (JSON) wired into a human-input prompt (content) fails the dataType
    /// dimension of the compiler matrix (doc 85 §6.1).
    /// </summary>
    private static AgentOrchestrationGraphDefinition CreateIncompatibleEdgeDraft()
        => new()
        {
            GraphId = "graph-edge",
            RevisionId = "graph-edge/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Edge port incompatibility 422.",
            MaxConcurrency = 1,
            Nodes =
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
                    Gate = new AgentOrchestrationGateDefinition { EvaluatorId = "pudding.gate.approval/v1" },
                    ExpectedOutputContract = AgentOrchestrationDataTypes.Json
                },
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
                    ExpectedOutputContract = AgentOrchestrationDataTypes.Content
                }
            ],
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "gate-to-ask",
                    FromNodeId = "gate",
                    ToNodeId = "ask",
                    Kind = AgentOrchestrationEdgeKind.Data,
                    Bindings =
                    [
                        new AgentOrchestrationDataBinding
                        {
                            SourcePortId = "decision",
                            TargetPortId = "prompt"
                        }
                    ]
                }
            ]
        };

    private static string ComputeSha256(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class RecordingSignal : IAgentOrchestrationCommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string runId, long knownHead, CancellationToken ct)
            => ValueTask.CompletedTask;
        public void Signal(string runId, long committedThroughSequence)
        {
        }
    }
}