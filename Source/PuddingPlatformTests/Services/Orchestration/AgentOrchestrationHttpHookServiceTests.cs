using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Orchestration;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Services.Orchestration;

[TestClass]
public sealed class AgentOrchestrationHttpHookServiceTests
{
    private string _testRoot = null!;
    private SqliteAgentOrchestrationStore _store = null!;
    private AgentOrchestrationHttpHookService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "orchestration-http-hook-tests",
            Guid.NewGuid().ToString("N"));
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

        var registry = AgentOrchestrationComponentRegistry.Default;
        _store = new SqliteAgentOrchestrationStore(
            factory,
            new AgentOrchestrationGraphCompiler(registry),
            new NoOpSignal(),
            TimeProvider.System,
            NullLogger<SqliteAgentOrchestrationStore>.Instance);
        _service = new AgentOrchestrationHttpHookService(
            _store,
            _store,
            NullLogger<AgentOrchestrationHttpHookService>.Instance);
        var saved = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = CreateDefinition(registry),
            ExpectedCurrentRevision = 0
        });
        Assert.IsTrue(saved.Success, saved.ErrorMessage);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public async Task Invoke_MapsPayload_CreatesActiveRun_AndIsIdempotent()
    {
        var request = new AgentOrchestrationHttpHookInvokeRequest
        {
            SourceEventId = "debug-event-1",
            Payload = JsonSerializer.SerializeToElement(new { message = "hello" })
        };

        var first = await _service.InvokeAsync(
            "graph-hook",
            "graph-hook/r001",
            "api-hook",
            request,
            "admin");
        var retry = await _service.InvokeAsync(
            "graph-hook",
            "graph-hook/r001",
            "api-hook",
            request,
            "admin");
        var conflict = await _service.InvokeAsync(
            "graph-hook",
            "graph-hook/r001",
            "api-hook",
            request with
            {
                Payload = JsonSerializer.SerializeToElement(new { message = "changed" })
            },
            "admin");

        Assert.AreEqual(AgentOrchestrationHttpHookResultKind.Success, first.Kind);
        Assert.IsTrue(first.Receipt!.Created);
        Assert.IsTrue(first.Receipt.Activated);
        Assert.AreEqual(AgentOrchestrationRunStatus.Active, first.Receipt.Run.Status);
        Assert.AreEqual(
            "hello",
            first.Receipt.Run.Inputs["request"].InlineValue!.Value.GetString());
        Assert.AreEqual(AgentOrchestrationNodeRunStatus.Ready, first.Receipt.Run.Nodes.Single().Status);
        Assert.AreEqual(AgentOrchestrationHttpHookResultKind.Success, retry.Kind);
        Assert.IsFalse(retry.Receipt!.Created);
        Assert.AreEqual(first.Receipt.Run.RunId, retry.Receipt.Run.RunId);
        Assert.AreEqual(AgentOrchestrationHttpHookResultKind.Conflict, conflict.Kind);
        Assert.AreEqual("orchestration.run_input_conflict", conflict.ErrorCode);
    }

    [TestMethod]
    public async Task Invoke_RequiresExplicitMatchingRevisionAndWebhookTrigger()
    {
        var request = new AgentOrchestrationHttpHookInvokeRequest
        {
            SourceEventId = "debug-event-2",
            Payload = JsonSerializer.SerializeToElement(new { message = "hello" })
        };

        var missingRevision = await _service.InvokeAsync(
            "graph-hook",
            "graph-hook/r999",
            "api-hook",
            request,
            "admin");
        var missingTrigger = await _service.InvokeAsync(
            "graph-hook",
            "graph-hook/r001",
            "not-there",
            request,
            "admin");

        Assert.AreEqual(AgentOrchestrationHttpHookResultKind.NotFound, missingRevision.Kind);
        Assert.AreEqual(AgentOrchestrationHttpHookResultKind.NotFound, missingTrigger.Kind);
    }

    private static AgentOrchestrationGraphDefinition CreateDefinition(
        IAgentOrchestrationComponentRegistry registry)
    {
        Assert.IsTrue(registry.TryResolveComponent(
            AgentOrchestrationComponentTypes.HumanInput,
            "1",
            out var component));
        Assert.IsTrue(registry.TryResolveTrigger(
            AgentOrchestrationTriggerTypes.Webhook,
            "1",
            out var trigger));
        return new AgentOrchestrationGraphDefinition
        {
            GraphId = "graph-hook",
            RevisionId = "graph-hook/r001",
            Revision = 1,
            WorkspaceId = "default",
            RootSessionId = "session-hook",
            CreatedByAgentId = "admin",
            Objective = "Exercise the debug HTTP hook.",
            Inputs =
            [
                new AgentOrchestrationGraphInput
                {
                    InputId = "request",
                    Contract = new AgentOrchestrationDataContract
                    {
                        DataType = AgentOrchestrationDataTypes.Content,
                        MediaTypes = ["application/json"],
                        Cardinality = AgentOrchestrationPortCardinality.One,
                        Deliveries = [AgentOrchestrationValueDelivery.Inline]
                    }
                }
            ],
            Triggers =
            [
                new AgentOrchestrationTriggerDefinition
                {
                    TriggerId = "api-hook",
                    Trigger = new AgentOrchestrationTriggerReference
                    {
                        TriggerType = AgentOrchestrationTriggerTypes.Webhook,
                        Version = "1",
                        ContractHash = trigger.ContractHash
                    },
                    InputBindings =
                    [
                        new AgentOrchestrationTriggerInputBinding
                        {
                            SourcePath = "$.message",
                            TargetInputId = "request"
                        }
                    ]
                }
            ],
            Nodes =
            [
                new AgentOrchestrationNodeDefinition
                {
                    NodeId = "start",
                    Kind = AgentOrchestrationNodeKind.HumanInput,
                    Title = "Start",
                    Objective = "Accept the hook payload.",
                    Component = new AgentOrchestrationComponentReference
                    {
                        ComponentType = component.Descriptor.ComponentType,
                        Version = component.Descriptor.Version,
                        ContractHash = component.ContractHash
                    },
                    GraphInputBindings =
                    [
                        new AgentOrchestrationGraphInputBinding
                        {
                            InputId = "request",
                            TargetPortId = "prompt"
                        }
                    ],
                    ExpectedOutputContract = AgentOrchestrationDataTypes.Content,
                    FailureBehavior = AgentOrchestrationFailureBehavior.AwaitDecision
                }
            ]
        };
    }

    private sealed class NoOpSignal : IAgentOrchestrationCommittedEventSignal
    {
        public void Signal(string runId, long committedThroughSequence)
        {
        }

        public ValueTask WaitForChangeAsync(string runId, long knownHead, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
