using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Orchestration;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Services.Orchestration;

/// <summary>
/// S1 server-side revision authoring (doc 85 §5.1): server-authored audit fields, head CAS,
/// stale-head conflicts, and compile diagnostics. Client-forged revision identity is never trusted.
/// </summary>
[TestClass]
public sealed class AgentOrchestrationAuthoringServiceTests
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
            "orchestration-authoring-tests",
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
    public async Task CreateRevision_ServerAuthorsNextRevisionWithAuditFields()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var draft = CreateDefinition() with
        {
            Objective = "Second revision objective",
            RevisionId = "graph-001/r999",          // client forged
            Revision = 99,                           // client forged
            ParentRevisionId = "graph-001/r777",     // client forged
            CreatedByAgentId = "evil-client",        // client forged
            CreatedAtUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, result.Status);
        var saved = result.Value!;
        Assert.AreEqual("graph-001/r002", saved.RevisionId);
        Assert.AreEqual(2, saved.Revision);
        Assert.AreEqual("graph-001/r001", saved.ParentRevisionId);
        Assert.AreEqual("admin-user", saved.CreatedByAgentId);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero), saved.CreatedAtUtc);
        Assert.AreEqual("Second revision objective", saved.Objective);
        Assert.AreEqual("default", saved.WorkspaceId);
        Assert.AreEqual("session-001", saved.RootSessionId);
        Assert.IsTrue(saved.Nodes.All(node => node.Component.ContractHash?.StartsWith("sha256:") == true));
    }

    [TestMethod]
    public async Task CreateRevision_StaleHead_ReturnsConflictWithCurrentRevisionId()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var stale = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 2,
                Definition = CreateDefinition()
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.Conflict, stale.Status);
        Assert.AreEqual("orchestration.revision_conflict", stale.ErrorCode);
        Assert.AreEqual(1L, stale.CurrentVersion);
        Assert.AreEqual("graph-001/r001", stale.CurrentRevisionId);
        var latestAfterStale = await _store.GetLatestRevisionAsync("graph-001");
        Assert.AreEqual(1, latestAfterStale!.Revision);
    }

    [TestMethod]
    public async Task CreateRevision_UnknownGraph_ReturnsNotFound()
    {
        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "missing-graph",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { GraphId = "missing-graph" }
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.NotFound, result.Status);
        Assert.AreEqual("orchestration.graph_not_found", result.ErrorCode);
    }

    [TestMethod]
    public async Task CreateRevision_RouteGraphMismatch_IsRejectedBeforeAnyRead()
    {
        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "route-graph",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { GraphId = "other-graph" }
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.AreEqual("orchestration.revision_graph_mismatch", result.ErrorCode);
    }

    [TestMethod]
    public async Task CreateRevision_UnknownComponent_ReturnsDiagnosticsAndSavesNothing()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var draft = CreateDefinition() with
        {
            Nodes =
            [
                CreateNode("worker", maxAttempts: 1) with
                {
                    Component = new AgentOrchestrationComponentReference
                    {
                        ComponentType = "missing.component",
                        Version = "9"
                    }
                }
            ]
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.AreEqual("orchestration.definition_invalid", result.ErrorCode);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_component_unknown"));
        var latestAfterUnknown = await _store.GetLatestRevisionAsync("graph-001");
        Assert.AreEqual(1, latestAfterUnknown!.Revision);
    }

    [TestMethod]
    public async Task CreateRevision_DuplicateNodeIdAndInvalidRoute_ReturnDiagnostics()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var draft = CreateDefinition() with
        {
            Nodes =
            [
                CreateNode("worker", maxAttempts: 1),
                CreateNode("worker", maxAttempts: 1) with
                {
                    Executor = new AgentOrchestrationExecutorBinding
                    {
                        Kind = AgentOrchestrationExecutorKind.SubAgent,
                        Role = "specialist",
                        TemplateId = "specialist",
                        RouteKey = "invalid-route"
                    }
                }
            ]
        };

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = draft
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, result.Status);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_id_duplicate"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "graph.node_route_invalid"));
        Assert.IsNull(await _store.GetRevisionAsync("graph-001/r002"));
    }

    [TestMethod]
    public async Task CreateRevision_DoesNotMutatePreviousRevisionContentHash()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var before = (await _store.GetRevisionAsync("graph-001/r001"))!;
        var beforeJson = JsonSerializer.Serialize(before, AgentOrchestrationJson.CreateSerializerOptions());
        var beforeHash = ComputeSha256(beforeJson);

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { Objective = "Edited content" }
            },
            "admin-user",
            default);

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, result.Status);
        var after = (await _store.GetRevisionAsync("graph-001/r001"))!;
        var afterJson = JsonSerializer.Serialize(after, AgentOrchestrationJson.CreateSerializerOptions());
        Assert.AreEqual(beforeJson, afterJson);
        Assert.AreEqual(beforeHash, ComputeSha256(afterJson));
        Assert.AreEqual("Execute a durable two-node graph.", after.Objective);
    }

    [TestMethod]
    public async Task CreateRevision_DoesNotCreateRunsOrEvents()
    {
        await SaveDefinitionAsync(CreateDefinition());
        await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { Objective = "No run facts" }
            },
            "admin-user",
            default);

        var runs = await _store.ListRunsAsync(null, "graph-001", null, 10, 0);
        var events = await _store.GetEventsAfterAsync("graph-001", 0, 10);

        Assert.AreEqual(0, runs.Count);
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task CreateRevision_GraphWithRun_AllowsAppendButNotDelete()
    {
        await SaveDefinitionAsync(CreateDefinition());
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-001",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });

        var result = await _service.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { Objective = "Append after run" }
            },
            "admin-user",
            default);
        var deleted = await _store.DeleteGraphAsync(new AgentOrchestrationGraphDeleteRequest
        {
            GraphId = "graph-001",
            ExpectedCurrentRevision = 2
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, result.Status);
        Assert.AreEqual("graph-001/r002", result.Value!.RevisionId);
        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, deleted.Status);
        Assert.AreEqual("orchestration.graph_has_runs", deleted.ErrorCode);
        Assert.IsNotNull(await _store.GetLatestRevisionAsync("graph-001"));
        Assert.IsNotNull(await _store.GetRunAsync("run-001"));
    }

    [TestMethod]
    public async Task Validate_ReturnsNormalizedDefinitionAndDiagnosticsWithoutWriting()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var valid = await _service.ValidateAsync(new AgentOrchestrationDraftValidateRequest
        {
            GraphId = "graph-001",
            BaseRevisionId = "graph-001/r001",
            Definition = CreateDefinition() with { Objective = "Validated draft" }
        });
        var invalid = await _service.ValidateAsync(new AgentOrchestrationDraftValidateRequest
        {
            GraphId = "graph-001",
            Definition = CreateDefinition() with
            {
                Nodes =
                [
                    CreateNode("worker", maxAttempts: 1),
                    CreateNode("worker", maxAttempts: 1)
                ]
            }
        });

        Assert.IsTrue(valid.IsValid);
        Assert.IsNotNull(valid.NormalizedDefinition);
        CollectionAssert.AreEqual(new[] { "root", "child" }, valid.TopologicalNodeIds.ToArray());
        Assert.IsFalse(invalid.IsValid);
        Assert.IsTrue(invalid.Issues.Any(issue => issue.Code == "graph.node_id_duplicate"));
        Assert.IsNull(await _store.GetRevisionAsync("graph-001/r002"));
        var latestAfterValidate = await _store.GetLatestRevisionAsync("graph-001");
        Assert.AreEqual(1, latestAfterValidate!.Revision);
    }

    [TestMethod]
    public async Task ConcurrentSameHead_OnlyOneRevisionIsApplied()
    {
        await SaveDefinitionAsync(CreateDefinition());
        var results = await Task.WhenAll(
            _service.CreateRevisionAsync(new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { Objective = "Concurrent A" }
            }, "admin-a", default),
            _service.CreateRevisionAsync(new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = "graph-001",
                ExpectedCurrentRevision = 1,
                Definition = CreateDefinition() with { Objective = "Concurrent B" }
            }, "admin-b", default));

        Assert.HasCount(1, results.Where(result => result.Status == AgentOrchestrationStoreStatus.Applied));
        Assert.HasCount(1, results.Where(result => result.Status == AgentOrchestrationStoreStatus.Conflict));
        var revisions = await _store.ListRevisionsAsync("graph-001", 10);
        Assert.AreEqual(2, revisions.Count);
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

    private static AgentOrchestrationGraphDefinition CreateDefinition()
        => new()
        {
            GraphId = "graph-001",
            RevisionId = "graph-001/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Execute a durable two-node graph.",
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
                CreateNode("root", maxAttempts: 2),
                CreateNode("child", maxAttempts: 1)
            ],
            Edges =
            [
                new AgentOrchestrationEdgeDefinition
                {
                    EdgeId = "root-to-child",
                    FromNodeId = "root",
                    ToNodeId = "child",
                    Kind = AgentOrchestrationEdgeKind.Control
                }
            ],
            CreatedAtUtc = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero)
        };

    private static AgentOrchestrationNodeDefinition CreateNode(string nodeId, int maxAttempts)
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
            MaxAttempts = maxAttempts
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
