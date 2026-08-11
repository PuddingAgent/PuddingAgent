using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Orchestration;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Services.Orchestration;

[TestClass]
public sealed class AgentOrchestrationManualRunServiceTests
{
    private string _testRoot = null!;
    private SqliteAgentOrchestrationStore _store = null!;
    private AgentOrchestrationManualRunService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", "manual-run-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        var factory = new PlatformDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        _store = new SqliteAgentOrchestrationStore(
            factory,
            new AgentOrchestrationGraphCompiler(AgentOrchestrationComponentRegistry.Default),
            new NoOpSignal(),
            TimeProvider.System,
            NullLogger<SqliteAgentOrchestrationStore>.Instance);
        _service = new AgentOrchestrationManualRunService(
            _store,
            _store,
            NullLogger<AgentOrchestrationManualRunService>.Instance);
        var saved = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = CreateDefinition(),
            ExpectedCurrentRevision = 0
        });
        Assert.IsTrue(saved.Success, saved.ErrorMessage);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public async Task Start_PinsRevision_ActivatesRun_AndIsIdempotent()
    {
        var request = new AgentOrchestrationManualRunRequest
        {
            GraphId = "graph-image",
            RevisionId = "graph-image/r001",
            RequestId = "admin-click-001",
            Inputs = new Dictionary<string, AgentOrchestrationValueEnvelope>
            {
                ["prompt"] = new()
                {
                    DataType = AgentOrchestrationDataTypes.Content,
                    ContentType = "text/plain",
                    InlineValue = JsonSerializer.SerializeToElement("draw a lighthouse")
                }
            }
        };

        var first = await _service.StartAsync(request, "admin");
        var retry = await _service.StartAsync(request, "admin");

        Assert.AreEqual(AgentOrchestrationManualRunResultKind.Success, first.Kind);
        Assert.IsTrue(first.Receipt!.Created);
        Assert.IsTrue(first.Receipt.Activated);
        Assert.AreEqual(AgentOrchestrationRunStatus.Active, first.Receipt.Run.Status);
        Assert.AreEqual(AgentOrchestrationNodeRunStatus.Ready, first.Receipt.Run.Nodes.Single().Status);
        Assert.AreEqual("draw a lighthouse", first.Receipt.Run.Inputs["prompt"].InlineValue!.Value.GetString());
        Assert.AreEqual(AgentOrchestrationManualRunResultKind.Success, retry.Kind);
        Assert.IsFalse(retry.Receipt!.Created);
        Assert.AreEqual(first.Receipt.Run.RunId, retry.Receipt.Run.RunId);
    }

    private static AgentOrchestrationGraphDefinition CreateDefinition()
        => new()
        {
            GraphId = "graph-image",
            RevisionId = "graph-image/r001",
            WorkspaceId = "default",
            RootSessionId = "root-image",
            CreatedByAgentId = "admin",
            Objective = "Generate an image.",
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = "prompt",
                    Contract = new AgentOrchestrationDataContract
                    {
                        DataType = AgentOrchestrationDataTypes.Content,
                        MediaTypes = ["text/plain"],
                        Deliveries = [AgentOrchestrationValueDelivery.Inline]
                    }
                }
            ],
            Nodes =
            [
                new AgentOrchestrationNodeDefinition
                {
                    NodeId = "image",
                    Kind = AgentOrchestrationNodeKind.Tool,
                    Title = "Generate image",
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
                        new AgentOrchestrationGraphInputBinding
                        {
                            InputId = "prompt",
                            TargetPortId = "prompt"
                        }
                    ],
                    ExpectedOutputContract = AgentOrchestrationDataTypes.Artifact
                }
            ]
        };

    private sealed class NoOpSignal : IAgentOrchestrationCommittedEventSignal
    {
        public void Signal(string runId, long committedThroughSequence)
        {
        }

        public ValueTask WaitForChangeAsync(string runId, long knownHead, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
