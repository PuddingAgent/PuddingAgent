using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Orchestration;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// S1 revision authoring HTTP contract (doc 85 §5.1): route/payload graphId consistency, 201 on
/// applied head CAS, 409 with current head facts on conflict, 422 with diagnostics on compile
/// failure, and 404 for unknown graphs. Validation is side-effect free.
/// </summary>
[TestClass]
public sealed class AgentOrchestrationRevisionApiControllerTests
{
    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private MutableTimeProvider _timeProvider = null!;
    private RecordingSignal _signal = null!;
    private SqliteAgentOrchestrationStore _store = null!;
    private AgentOrchestrationRevisionApiController _controller = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "orchestration-revision-api-tests",
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
        var service = new AgentOrchestrationAuthoringService(
            _store,
            new AgentOrchestrationGraphCompiler(),
            _timeProvider);
        _controller = new AgentOrchestrationRevisionApiController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public async Task Put_Applied_Returns201WithServerAuthoredRevision()
    {
        await SaveDefinitionAsync();
        var action = await _controller.PutRevision(
            "graph-001",
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = CreateDefinition() with { Objective = "r2 objective" },
                ExpectedCurrentRevision = 1
            },
            default);

        var result = (JsonResult)action.Result!;
        var definition = (AgentOrchestrationGraphDefinition)result.Value!;
        Assert.AreEqual(StatusCodes.Status201Created, result.StatusCode);
        Assert.AreEqual("graph-001/r002", definition.RevisionId);
        Assert.AreEqual(2, definition.Revision);
        Assert.AreEqual("graph-001/r001", definition.ParentRevisionId);
        Assert.AreEqual("admin-ui", definition.CreatedByAgentId);
    }

    [TestMethod]
    public async Task Put_RouteGraphMismatch_Returns400()
    {
        var action = await _controller.PutRevision(
            "other-graph",
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = CreateDefinition(),
                ExpectedCurrentRevision = 1
            },
            default);

        var badRequest = (BadRequestObjectResult)action.Result!;
        Assert.AreEqual(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [TestMethod]
    public async Task Put_StaleHead_Returns409WithCurrentRevisionId()
    {
        await SaveDefinitionAsync();
        var action = await _controller.PutRevision(
            "graph-001",
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = CreateDefinition() with { Objective = "stale draft" },
                ExpectedCurrentRevision = 5
            },
            default);

        var conflict = (ConflictObjectResult)action.Result!;
        Assert.AreEqual(StatusCodes.Status409Conflict, conflict.StatusCode);
        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(conflict.Value, AgentOrchestrationJson.CreateSerializerOptions()));
        Assert.AreEqual("orchestration.revision_conflict", payload.GetProperty("code").GetString());
        Assert.AreEqual(1, payload.GetProperty("currentRevision").GetInt32());
        Assert.AreEqual("graph-001/r001", payload.GetProperty("currentRevisionId").GetString());
    }

    [TestMethod]
    public async Task Put_CompileFailure_Returns422WithDiagnostics()
    {
        await SaveDefinitionAsync();
        var action = await _controller.PutRevision(
            "graph-001",
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = CreateDefinition() with
                {
                    Nodes =
                    [
                        CreateNode("worker", maxAttempts: 1),
                        CreateNode("worker", maxAttempts: 1)
                    ]
                },
                ExpectedCurrentRevision = 1
            },
            default);

        var unprocessable = (UnprocessableEntityObjectResult)action.Result!;
        Assert.AreEqual(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);
        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(unprocessable.Value, AgentOrchestrationJson.CreateSerializerOptions()));
        Assert.AreEqual("orchestration.definition_invalid", payload.GetProperty("code").GetString());
        var issues = payload.GetProperty("issues");
        Assert.IsTrue(issues.GetArrayLength() > 0);
        var codes = issues.EnumerateArray().Select(issue => issue.GetProperty("code").GetString()).ToArray();
        CollectionAssert.Contains(codes, "graph.node_id_duplicate");
        var duplicateIssue = issues.EnumerateArray().First(issue => issue.GetProperty("code").GetString() == "graph.node_id_duplicate");
        Assert.AreEqual("error", duplicateIssue.GetProperty("severity").GetString());
        Assert.AreEqual("node", duplicateIssue.GetProperty("elementType").GetString());
        Assert.IsNull(await _store.GetRevisionAsync("graph-001/r002"));
    }

    [TestMethod]
    public async Task Put_UnknownGraph_Returns404()
    {
        var action = await _controller.PutRevision(
            "missing-graph",
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = CreateDefinition() with { GraphId = "missing-graph" },
                ExpectedCurrentRevision = 0
            },
            default);

        var notFound = (NotFoundObjectResult)action.Result!;
        Assert.AreEqual(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    [TestMethod]
    public async Task Validate_Returns200WithIssuesAndTopologicalNodeIds()
    {
        await SaveDefinitionAsync();
        var validAction = await _controller.ValidateDraft(
            "graph-001",
            new AgentOrchestrationDraftValidateRequest
            {
                GraphId = "graph-001",
                BaseRevisionId = "graph-001/r001",
                Definition = CreateDefinition() with { Objective = "Valid draft" }
            },
            default);
        var validResult = (JsonResult)validAction.Result!;
        var validDto = (AgentOrchestrationDraftValidationResultDto)validResult.Value!;
        Assert.IsTrue(validDto.IsValid);
        Assert.IsNotNull(validDto.NormalizedDefinition);
        CollectionAssert.AreEqual(new[] { "root", "child" }, validDto.TopologicalNodeIds.ToArray());

        var invalidAction = await _controller.ValidateDraft(
            "graph-001",
            new AgentOrchestrationDraftValidateRequest
            {
                GraphId = "graph-001",
                Definition = CreateDefinition() with
                {
                    Nodes = [CreateNode("worker", maxAttempts: 1) with { Executor = null }]
                }
            },
            default);
        var invalidResult = (JsonResult)invalidAction.Result!;
        var invalidDto = (AgentOrchestrationDraftValidationResultDto)invalidResult.Value!;
        Assert.IsFalse(invalidDto.IsValid);
        Assert.IsTrue(invalidDto.Issues.Any(issue => issue.Code == "graph.node_executor_required"));
        Assert.IsNull(await _store.GetRevisionAsync("graph-001/r002"));
    }

    [TestMethod]
    public async Task ValidateJson_WebEnumPayload_UsesOrchestrationSerializer()
    {
        var request = new AgentOrchestrationDraftValidateRequest
        {
            GraphId = "graph-001",
            BaseRevisionId = "graph-001/r001",
            Definition = CreateDefinition()
        };
        var body = JsonSerializer.SerializeToElement(
            request,
            AgentOrchestrationJson.CreateSerializerOptions());

        var action = await _controller.ValidateDraftJson("graph-001", body, default);

        var result = (JsonResult)action.Result!;
        var dto = (AgentOrchestrationDraftValidationResultDto)result.Value!;
        Assert.IsTrue(dto.IsValid);
        Assert.AreEqual(AgentOrchestrationNodeKind.SubAgent, dto.NormalizedDefinition!.Nodes[0].Kind);
    }

    [TestMethod]
    public async Task PutJson_WebEnumPayload_Returns201()
    {
        await SaveDefinitionAsync();
        var request = new AgentOrchestrationRevisionWriteRequest
        {
            Definition = CreateDefinition() with { Objective = "JSON r2 objective" },
            ExpectedCurrentRevision = 1
        };
        var body = JsonSerializer.SerializeToElement(
            request,
            AgentOrchestrationJson.CreateSerializerOptions());

        var action = await _controller.PutRevisionJson("graph-001", body, default);

        var result = (JsonResult)action.Result!;
        var definition = (AgentOrchestrationGraphDefinition)result.Value!;
        Assert.AreEqual(StatusCodes.Status201Created, result.StatusCode);
        Assert.AreEqual("graph-001/r002", definition.RevisionId);
    }

    [TestMethod]
    public async Task Validate_EdgePortIncompatible_ProjectsElementTypeElementIdAndPortId()
    {
        var action = await _controller.ValidateDraft(
            "graph-001",
            new AgentOrchestrationDraftValidateRequest
            {
                GraphId = "graph-001",
                Definition = CreateIncompatibleEdgeDefinition()
            },
            default);

        var result = (JsonResult)action.Result!;
        var dto = (AgentOrchestrationDraftValidationResultDto)result.Value!;
        Assert.IsFalse(dto.IsValid);
        var issue = dto.Issues.First(item => item.Code == "graph.data_ports_incompatible");
        Assert.AreEqual("error", issue.Severity);
        Assert.AreEqual("edge", issue.ElementType);
        Assert.AreEqual("gate-to-ask", issue.ElementId);
        Assert.AreEqual("prompt", issue.PortId);
        Assert.IsNull(await _store.GetRevisionAsync("graph-001/r002"));
    }

    [TestMethod]
    public async Task Validate_GraphInputIncompatible_ProjectsNodeElementAndPortId()
    {
        var action = await _controller.ValidateDraft(
            "graph-001",
            new AgentOrchestrationDraftValidateRequest
            {
                GraphId = "graph-001",
                Definition = CreateIncompatibleGraphInputDefinition()
            },
            default);

        var result = (JsonResult)action.Result!;
        var dto = (AgentOrchestrationDraftValidationResultDto)result.Value!;
        Assert.IsFalse(dto.IsValid);
        var issue = dto.Issues.First(item => item.Code == "graph.node_input_port_incompatible");
        Assert.AreEqual("error", issue.Severity);
        Assert.AreEqual("node", issue.ElementType);
        Assert.AreEqual("worker", issue.ElementId);
        Assert.AreEqual("context", issue.PortId);
        Assert.IsNull(await _store.GetRevisionAsync("graph-001/r002"));
    }

    [TestMethod]
    public async Task Validate_RouteMismatch_Returns400()
    {
        var action = await _controller.ValidateDraft(
            "other-graph",
            new AgentOrchestrationDraftValidateRequest
            {
                GraphId = "graph-001",
                Definition = CreateDefinition()
            },
            default);

        Assert.IsInstanceOfType<BadRequestObjectResult>(action.Result);
    }

    [TestMethod]
    public async Task Put_IgnoresClientForgedAuditFields()
    {
        await SaveDefinitionAsync();
        var forged = CreateDefinition() with
        {
            RevisionId = "graph-001/r999",
            Revision = 99,
            ParentRevisionId = "graph-001/r888",
            CreatedByAgentId = "evil-client",
            CreatedAtUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var action = await _controller.PutRevision(
            "graph-001",
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = forged,
                ExpectedCurrentRevision = 1
            },
            default);

        var result = (JsonResult)action.Result!;
        var definition = (AgentOrchestrationGraphDefinition)result.Value!;
        Assert.AreEqual(StatusCodes.Status201Created, result.StatusCode);
        Assert.AreEqual("graph-001/r002", definition.RevisionId);
        Assert.AreEqual(2, definition.Revision);
        Assert.AreEqual("graph-001/r001", definition.ParentRevisionId);
        Assert.AreNotEqual("evil-client", definition.CreatedByAgentId);
        var stored = await _store.GetRevisionAsync("graph-001/r002");
        Assert.IsNotNull(stored);
        Assert.AreEqual("admin-ui", stored!.CreatedByAgentId);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero), stored.CreatedAtUtc);
    }

    private SqliteAgentOrchestrationStore CreateStore()
        => new(
            _dbFactory,
            new AgentOrchestrationGraphCompiler(),
            _signal,
            _timeProvider,
            NullLogger<SqliteAgentOrchestrationStore>.Instance);

    private async Task SaveDefinitionAsync()
    {
        var result = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = CreateDefinition(),
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

    /// <summary>
    /// Gate decision (JSON) wired into a human-input prompt (content) must be rejected as a
    /// port-incompatible data edge (doc 85 §6.1 dataType dimension).
    /// </summary>
    private static AgentOrchestrationGraphDefinition CreateIncompatibleEdgeDefinition()
        => new()
        {
            GraphId = "graph-001",
            RevisionId = "graph-001/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Edge port incompatibility projection.",
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

    /// <summary>
    /// A JSON graph input bound to the sub-agent's content context port must be rejected as
    /// incompatible while the Any-typed request port stays bound (doc 85 §6.1 graph input).
    /// </summary>
    private static AgentOrchestrationGraphDefinition CreateIncompatibleGraphInputDefinition()
        => new()
        {
            GraphId = "graph-001",
            RevisionId = "graph-001/r001",
            WorkspaceId = "default",
            RootSessionId = "session-001",
            CreatedByAgentId = "main-agent",
            Objective = "Graph input incompatibility projection.",
            MaxConcurrency = 1,
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = "gi",
                    Contract = new AgentOrchestrationDataContract
                    {
                        DataType = AgentOrchestrationDataTypes.Json,
                        MediaTypes = ["application/json"],
                        Deliveries = [AgentOrchestrationValueDelivery.Inline]
                    }
                }
            ],
            Nodes =
            [
                CreateNode("worker", maxAttempts: 1) with
                {
                    GraphInputBindings =
                    [
                        new AgentOrchestrationGraphInputBinding
                        {
                            InputId = "gi",
                            TargetPortId = "request"
                        },
                        new AgentOrchestrationGraphInputBinding
                        {
                            InputId = "gi",
                            TargetPortId = "context"
                        }
                    ]
                }
            ]
        };

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
