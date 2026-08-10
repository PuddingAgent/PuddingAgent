using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Orchestration;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Orchestration;
using System.Text.Json;

namespace PuddingPlatformTests.Services.Orchestration;

[TestClass]
public sealed class SqliteAgentOrchestrationStoreTests
{
    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private MutableTimeProvider _timeProvider = null!;
    private RecordingSignal _signal = null!;
    private SqliteAgentOrchestrationStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "orchestration-store-tests",
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
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public async Task SaveRevision_UsesImmutableContentAndGraphHeadCompareExchange()
    {
        var revision1 = CreateDefinition();

        var first = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = revision1,
            ExpectedCurrentRevision = 0
        });
        var retry = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = revision1,
            ExpectedCurrentRevision = 0
        });
        var revision2 = revision1 with
        {
            RevisionId = "graph-001/r002",
            Revision = 2,
            ParentRevisionId = revision1.RevisionId,
            Objective = "Revised objective"
        };
        var stale = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = revision2,
            ExpectedCurrentRevision = 0
        });
        var second = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = revision2,
            ExpectedCurrentRevision = 1
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, first.Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Unchanged, retry.Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Conflict, stale.Status);
        Assert.AreEqual(1L, stale.CurrentVersion);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, second.Status);
        Assert.AreEqual(revision2.RevisionId, (await _store.GetLatestRevisionAsync(revision1.GraphId))!.RevisionId);
        Assert.AreEqual(revision1.RevisionId, (await _store.GetRevisionAsync(revision1.RevisionId))!.RevisionId);
        var revisions = await _store.ListRevisionsAsync(revision1.GraphId, 100);
        CollectionAssert.AreEqual(
            new[] { revision2.RevisionId, revision1.RevisionId },
            revisions.Select(item => item.RevisionId).ToArray());
        Assert.IsTrue(revisions.All(item => item.ContentHash.Length == 64));
    }

    [TestMethod]
    public async Task SaveRevision_DoesNotCreateRunOrEventFacts()
    {
        await SaveDefinitionAsync();
        var revision2 = CreateDefinition() with
        {
            RevisionId = "graph-001/r002",
            Revision = 2,
            ParentRevisionId = "graph-001/r001",
            Objective = "Second revision"
        };

        var second = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = revision2,
            ExpectedCurrentRevision = 1
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, second.Status);
        var runs = await _store.ListRunsAsync(null, "graph-001", null, 10, 0);
        var events = await _store.GetEventsAfterAsync("graph-001", 0, 10);
        Assert.AreEqual(0, runs.Count);
        Assert.AreEqual(0, events.Count);
        Assert.AreEqual("graph-001/r002", (await _store.GetLatestRevisionAsync("graph-001"))!.RevisionId);
    }

    [TestMethod]
    public async Task SaveRevision_WithExistingRun_AllowsAppendButBlocksGraphDelete()
    {
        await SaveDefinitionAsync();
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-001",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var revision2 = CreateDefinition() with
        {
            RevisionId = "graph-001/r002",
            Revision = 2,
            ParentRevisionId = "graph-001/r001",
            Objective = "Append while a run exists"
        };

        var appended = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = revision2,
            ExpectedCurrentRevision = 1
        });
        var deleted = await _store.DeleteGraphAsync(new AgentOrchestrationGraphDeleteRequest
        {
            GraphId = "graph-001",
            ExpectedCurrentRevision = 2
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, appended.Status);
        Assert.AreEqual("graph-001/r002", (await _store.GetLatestRevisionAsync("graph-001"))!.RevisionId);
        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, deleted.Status);
        Assert.AreEqual("orchestration.graph_has_runs", deleted.ErrorCode);
        Assert.IsNotNull(await _store.GetRunAsync("run-001"));
    }

    [TestMethod]
    public async Task ConcurrentSameHeadRevisions_OnlyOneIsApplied()
    {
        await SaveDefinitionAsync();
        var draftA = CreateDefinition() with
        {
            RevisionId = "graph-001/r002",
            Revision = 2,
            ParentRevisionId = "graph-001/r001",
            Objective = "Concurrent A"
        };
        var draftB = draftA with { Objective = "Concurrent B" };

        var results = await Task.WhenAll(
            _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
            {
                Definition = draftA,
                ExpectedCurrentRevision = 1
            }),
            CreateStore().SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
            {
                Definition = draftB,
                ExpectedCurrentRevision = 1
            }));

        Assert.HasCount(1, results.Where(result => result.Status == AgentOrchestrationStoreStatus.Applied));
        Assert.HasCount(1, results.Where(result => result.Status == AgentOrchestrationStoreStatus.Conflict));
        var revisions = await _store.ListRevisionsAsync("graph-001", 10);
        Assert.AreEqual(2, revisions.Count);
        Assert.AreEqual(1, revisions.Count(revision => revision.Revision == 2));
    }

    [TestMethod]
    public async Task CreateAndActivateRun_CommitsProjectionAndContiguousEventsBeforeSignal()
    {
        await SaveDefinitionAsync();

        var created = await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-001",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var activated = await _store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
        {
            RunId = "run-001",
            ExpectedVersion = created.Value!.Version
        });
        var createRetry = await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-001",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var activationRetry = await _store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
        {
            RunId = "run-001",
            ExpectedVersion = created.Value.Version
        });

        Assert.AreEqual(AgentOrchestrationRunStatus.Draft, created.Value!.Status);
        Assert.AreEqual(1L, created.Value.HeadSequence);
        Assert.AreEqual(AgentOrchestrationRunStatus.Active, activated.Value!.Status);
        Assert.AreEqual(2L, activated.Value.Version);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Unchanged, createRetry.Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Unchanged, activationRetry.Status);
        Assert.AreEqual(AgentOrchestrationNodeRunStatus.Ready, activated.Value.Nodes.Single(node => node.NodeId == "root").Status);
        Assert.AreEqual(AgentOrchestrationNodeRunStatus.Pending, activated.Value.Nodes.Single(node => node.NodeId == "child").Status);

        var events = await _store.GetEventsAfterAsync("run-001", 0, 100);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, events.Select(item => item.Sequence).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                AgentOrchestrationEventTypes.RunCreated,
                AgentOrchestrationEventTypes.RunActivated,
                AgentOrchestrationEventTypes.NodeReady
            },
            events.Select(item => item.EventType).ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 3 }, _signal.Heads.ToArray());
    }

    [TestMethod]
    public async Task ClaimStartAndTerminalCommit_RejectsStaleFenceAndPreservesExecutionIdentity()
    {
        await CreateActiveRunAsync();
        var claim = (await _store.TryClaimNextReadyNodeAsync(new AgentOrchestrationNodeClaimRequest
        {
            RunId = "run-001",
            WorkerId = "worker-1",
            LeaseDuration = TimeSpan.FromMinutes(2)
        })).Value!;
        var started = await _store.MarkNodeRunningAsync(new AgentOrchestrationNodeStartRequest
        {
            RunId = claim.RunId,
            NodeId = claim.NodeId,
            ClaimId = claim.ClaimId,
            WorkerId = claim.WorkerId,
            FencingToken = claim.FencingToken,
            ExecutionRunId = "child-run-001",
            SubSessionId = "sub-session-001"
        });
        var stale = await _store.CommitNodeTerminalAsync(new AgentOrchestrationNodeTerminalRequest
        {
            RunId = claim.RunId,
            NodeId = claim.NodeId,
            ClaimId = claim.ClaimId,
            WorkerId = claim.WorkerId,
            FencingToken = claim.FencingToken + 1,
            Succeeded = true,
            Summary = "must not commit"
        });
        var completed = await _store.CommitNodeTerminalAsync(new AgentOrchestrationNodeTerminalRequest
        {
            RunId = claim.RunId,
            NodeId = claim.NodeId,
            ClaimId = claim.ClaimId,
            WorkerId = claim.WorkerId,
            FencingToken = claim.FencingToken,
            Succeeded = true,
            Summary = "completed",
            ArtifactReference = "artifact://root-output"
        });

        Assert.AreEqual(AgentOrchestrationNodeRunStatus.Running, started.Value!.Nodes.Single(node => node.NodeId == "root").Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Conflict, stale.Status);
        var node = completed.Value!.Nodes.Single(item => item.NodeId == "root");
        Assert.AreEqual(AgentOrchestrationNodeRunStatus.Completed, node.Status);
        Assert.AreEqual("child-run-001", node.ExecutionRunId);
        Assert.AreEqual("sub-session-001", node.SubSessionId);
        Assert.AreEqual("artifact://root-output", node.ArtifactReference);

        var events = await _store.GetEventsAfterAsync("run-001", 3, 100);
        CollectionAssert.AreEqual(new long[] { 4, 5, 6 }, events.Select(item => item.Sequence).ToArray());
        Assert.AreEqual("child-run-001", events.Single(item => item.EventType == AgentOrchestrationEventTypes.NodeStarted).ExecutionRunId);
        Assert.AreEqual("sub-session-001", events.Single(item => item.EventType == AgentOrchestrationEventTypes.NodeCompleted).SubSessionId);
    }

    [TestMethod]
    public async Task ExpiredClaim_IsRecoveredAfterStoreRestartAndOldFenceCannotCommit()
    {
        await CreateActiveRunAsync();
        var firstClaim = (await _store.TryClaimNextReadyNodeAsync(new AgentOrchestrationNodeClaimRequest
        {
            RunId = "run-001",
            WorkerId = "worker-1",
            LeaseDuration = TimeSpan.FromMinutes(1)
        })).Value!;
        _timeProvider.Advance(TimeSpan.FromMinutes(2));

        var restartedStore = CreateStore();
        var secondResult = await restartedStore.TryClaimNextReadyNodeAsync(new AgentOrchestrationNodeClaimRequest
        {
            RunId = "run-001",
            WorkerId = "worker-2",
            LeaseDuration = TimeSpan.FromMinutes(1)
        });
        var secondClaim = secondResult.Value!;
        var staleCompletion = await restartedStore.CommitNodeTerminalAsync(new AgentOrchestrationNodeTerminalRequest
        {
            RunId = firstClaim.RunId,
            NodeId = firstClaim.NodeId,
            ClaimId = firstClaim.ClaimId,
            WorkerId = firstClaim.WorkerId,
            FencingToken = firstClaim.FencingToken,
            Succeeded = true
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, secondResult.Status);
        Assert.AreEqual(firstClaim.NodeId, secondClaim.NodeId);
        Assert.AreEqual(2, secondClaim.Attempt);
        Assert.IsTrue(secondClaim.FencingToken > firstClaim.FencingToken);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Conflict, staleCompletion.Status);
        var events = await restartedStore.GetEventsAfterAsync("run-001", 4, 100);
        CollectionAssert.AreEqual(
            new[]
            {
                AgentOrchestrationEventTypes.NodeClaimExpired,
                AgentOrchestrationEventTypes.NodeClaimed
            },
            events.Select(item => item.EventType).ToArray());
        CollectionAssert.AreEqual(new long[] { 5, 6 }, events.Select(item => item.Sequence).ToArray());
    }

    [TestMethod]
    public async Task ConcurrentClaims_RespectRunMaxConcurrency()
    {
        var definition = CreateDefinition() with
        {
            MaxConcurrency = 1,
            Nodes =
            [
                CreateNode("root-a", maxAttempts: 1),
                CreateNode("root-b", maxAttempts: 1)
            ],
            Edges = Array.Empty<AgentOrchestrationEdgeDefinition>()
        };
        await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = definition,
            ExpectedCurrentRevision = 0
        });
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-001",
            RevisionId = definition.RevisionId,
            RequestedByAgentId = "main-agent"
        });
        await _store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
        {
            RunId = "run-001",
            ExpectedVersion = 1
        });

        var results = await Task.WhenAll(
            _store.TryClaimNextReadyNodeAsync(new AgentOrchestrationNodeClaimRequest
            {
                RunId = "run-001",
                WorkerId = "worker-a"
            }),
            CreateStore().TryClaimNextReadyNodeAsync(new AgentOrchestrationNodeClaimRequest
            {
                RunId = "run-001",
                WorkerId = "worker-b"
            }));

        Assert.HasCount(1, results.Where(result => result.Status == AgentOrchestrationStoreStatus.Applied));
        Assert.HasCount(1, results.Where(result => result.Status == AgentOrchestrationStoreStatus.NoWork));
        var run = await _store.GetRunAsync("run-001");
        Assert.HasCount(1, run!.Nodes.Where(node => node.Status == AgentOrchestrationNodeRunStatus.Claimed));
    }

    [TestMethod]
    public async Task EventFollower_ReplaysThenFollowsLiveCommitWithoutDuplicates()
    {
        await SaveDefinitionAsync();
        var liveSignal = new AgentOrchestrationCommittedEventSignal();
        var liveStore = CreateStore(liveSignal);
        var created = await liveStore.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-watch",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var follower = new AgentOrchestrationEventFollower(liveStore, liveSignal);
        await using var events = follower.FollowAsync("run-watch", 0).GetAsyncEnumerator();

        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(1L, events.Current.Sequence);
        Assert.AreEqual(AgentOrchestrationEventTypes.RunCreated, events.Current.EventType);

        var nextEvent = events.MoveNextAsync().AsTask();
        await liveStore.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
        {
            RunId = "run-watch",
            ExpectedVersion = created.Value!.Version
        });

        Assert.IsTrue(await nextEvent.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2L, events.Current.Sequence);
        Assert.AreEqual(AgentOrchestrationEventTypes.RunActivated, events.Current.EventType);
    }

    [TestMethod]
    public async Task EventFollower_FailsExplicitlyWhenDurableHeadContainsGap()
    {
        await SaveDefinitionAsync();
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-gap",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM orchestration_run_events WHERE run_id = 'run-gap'");
        }

        var follower = new AgentOrchestrationEventFollower(_store, _signal);
        await using var events = follower.FollowAsync("run-gap", 0).GetAsyncEnumerator();
        var exception = await Assert.ThrowsExactlyAsync<AgentOrchestrationEventGapException>(
            async () => await events.MoveNextAsync().AsTask());

        Assert.AreEqual(1L, exception.ExpectedSequence);
        Assert.AreEqual(1L, exception.ActualOrHeadSequence);
    }

    [TestMethod]
    public async Task CommittedEventSignal_RetainsHeadAndWakesAllCurrentWaiters()
    {
        var signal = new AgentOrchestrationCommittedEventSignal();
        signal.Signal("run-signal", 2);
        await signal.WaitForChangeAsync("run-signal", 1, CancellationToken.None);

        var first = signal.WaitForChangeAsync("run-signal", 2, CancellationToken.None).AsTask();
        var second = signal.WaitForChangeAsync("run-signal", 2, CancellationToken.None).AsTask();
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);

        signal.Signal("run-signal", 3);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ReadApi_UsesContractJsonAndExposesReplayCursorMetadata()
    {
        await SaveDefinitionAsync();
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-api",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var controller = CreateApiController(_store, _signal);

        var catalogResult = (JsonResult)controller.GetCatalog().Result!;
        var serializerOptions = (JsonSerializerOptions)catalogResult.SerializerSettings!;
        var catalogJson = JsonSerializer.Serialize(catalogResult.Value, serializerOptions);
        StringAssert.Contains(catalogJson, "\"nodeKind\":\"subAgent\"");

        var eventsAction = await controller.GetEvents("run-api", afterSequence: 0, limit: 100);
        var eventsResult = (JsonResult)eventsAction.Result!;
        var page = (AgentOrchestrationEventPageDto)eventsResult.Value!;
        Assert.AreEqual(1L, page.NextSequence);
        Assert.AreEqual(1L, page.HeadSequence);
        Assert.IsFalse(page.HasMore);
        Assert.HasCount(1, page.Events);
    }

    [TestMethod]
    public async Task ReadApiWatch_UsesLastEventIdAndWritesCanonicalSseFrame()
    {
        await SaveDefinitionAsync();
        var liveSignal = new AgentOrchestrationCommittedEventSignal();
        var liveStore = CreateStore(liveSignal);
        await liveStore.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-api-watch",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var controller = CreateApiController(liveStore, liveSignal);
        var body = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = body;
        controller.ControllerContext.HttpContext.Request.Headers["Last-Event-ID"] = "0";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await controller.Watch("run-api-watch", afterSequence: null, cancellation.Token);

        Assert.AreEqual("text/event-stream", controller.Response.ContentType);
        body.Position = 0;
        var frame = await new StreamReader(body).ReadToEndAsync();
        StringAssert.Contains(frame, "id: 1\n");
        StringAssert.Contains(frame, $"event: {AgentOrchestrationEventTypes.RunCreated}\n");
        StringAssert.Contains(frame, "\"sequence\":1");
    }

    [TestMethod]
    public async Task DiscoveryLists_FilterAndOrderGraphHeadsAndRunSummaries()
    {
        await SaveDefinitionAsync();
        var created = await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-default",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        await _store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
        {
            RunId = "run-default",
            ExpectedVersion = created.Value!.Version
        });

        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        var researchDefinition = CreateDefinition() with
        {
            GraphId = "graph-002",
            RevisionId = "graph-002/r001",
            WorkspaceId = "research",
            RootSessionId = "session-002",
            Objective = "Research graph"
        };
        await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = researchDefinition,
            ExpectedCurrentRevision = 0
        });
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-research",
            RevisionId = researchDefinition.RevisionId,
            RequestedByAgentId = "research-agent"
        });

        var allGraphs = await _store.ListGraphsAsync(null, 10, 0);
        var researchGraphs = await _store.ListGraphsAsync("research", 10, 0);
        var activeRuns = await _store.ListRunsAsync(
            workspaceId: "default",
            graphId: "graph-001",
            status: AgentOrchestrationRunStatus.Active,
            limit: 10,
            offset: 0);
        var researchRuns = await _store.ListRunsAsync(
            workspaceId: null,
            graphId: "graph-002",
            status: null,
            limit: 10,
            offset: 0);

        CollectionAssert.AreEqual(
            new[] { "graph-002", "graph-001" },
            allGraphs.Select(graph => graph.GraphId).ToArray());
        Assert.HasCount(1, researchGraphs);
        Assert.AreEqual("graph-002/r001", researchGraphs[0].CurrentRevisionId);
        Assert.AreEqual(1, allGraphs.Single(graph => graph.GraphId == "graph-001").ActiveRunCount);
        Assert.HasCount(1, activeRuns);
        Assert.AreEqual("run-default", activeRuns[0].RunId);
        Assert.HasCount(1, researchRuns);
        Assert.AreEqual(AgentOrchestrationRunStatus.Draft, researchRuns[0].Status);
    }

    [TestMethod]
    public async Task SaveLayout_UsesIndependentCasAndRejectsUnknownExecutableNodes()
    {
        await SaveDefinitionAsync();
        var firstLayout = new AgentOrchestrationGraphLayout
        {
            GraphId = "graph-001",
            BaseRevisionId = "graph-001/r001",
            LayoutRevision = 1,
            Viewport = new AgentOrchestrationViewport { X = 12, Y = 24, Zoom = 1.2 },
            Nodes =
            [
                new AgentOrchestrationNodeLayout { NodeId = "root", X = 100, Y = 200 },
                new AgentOrchestrationNodeLayout { NodeId = "child", X = 420, Y = 200 }
            ]
        };
        var applied = await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            Layout = firstLayout,
            ExpectedCurrentLayoutRevision = 0
        });
        var retry = await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            Layout = firstLayout,
            ExpectedCurrentLayoutRevision = 0
        });
        var secondLayout = firstLayout with
        {
            LayoutRevision = 2,
            Nodes =
            [
                firstLayout.Nodes[0] with { X = 140 },
                firstLayout.Nodes[1]
            ]
        };
        var stale = await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            Layout = secondLayout,
            ExpectedCurrentLayoutRevision = 0
        });
        var updated = await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            Layout = secondLayout,
            ExpectedCurrentLayoutRevision = 1
        });
        var invalid = await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            Layout = secondLayout with
            {
                LayoutRevision = 3,
                Nodes = [new AgentOrchestrationNodeLayout { NodeId = "unknown", X = 0, Y = 0 }]
            },
            ExpectedCurrentLayoutRevision = 2
        });
        var cyclic = await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            Layout = secondLayout with
            {
                LayoutRevision = 3,
                Nodes =
                [
                    new AgentOrchestrationNodeLayout
                    {
                        NodeId = "root",
                        ParentNodeId = "child",
                        X = 0,
                        Y = 0
                    },
                    new AgentOrchestrationNodeLayout
                    {
                        NodeId = "child",
                        ParentNodeId = "root",
                        X = 10,
                        Y = 10
                    }
                ]
            },
            ExpectedCurrentLayoutRevision = 2
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, applied.Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Unchanged, retry.Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Conflict, stale.Status);
        Assert.AreEqual(1L, stale.CurrentVersion);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, updated.Status);
        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, invalid.Status);
        Assert.AreEqual("orchestration.layout_unknown_node", invalid.ErrorCode);
        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, cyclic.Status);
        Assert.AreEqual("orchestration.layout_parent_cycle", cyclic.ErrorCode);
        var loaded = await _store.GetLayoutAsync("graph-001", "graph-001/r001");
        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded.LayoutRevision);
        Assert.AreEqual(140d, loaded.Nodes.Single(node => node.NodeId == "root").X);
        Assert.AreEqual("Execute a durable two-node graph.", (await _store.GetRevisionAsync("graph-001/r001"))!.Objective);
    }

    [TestMethod]
    public async Task SaveLayout_MissingImmutableRevision_DoesNotWaitForUnrelatedWriter()
    {
        await SaveDefinitionAsync();
        await using var blockerDb = await _dbFactory.CreateDbContextAsync();
        var blockerConnection = (SqliteConnection)blockerDb.Database.GetDbConnection();
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = (SqliteTransaction)await blockerConnection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);
        await using (var blockerCommand = blockerConnection.CreateCommand())
        {
            blockerCommand.Transaction = blockerTransaction;
            blockerCommand.CommandText =
                "UPDATE orchestration_graphs SET updated_at = updated_at WHERE graph_id = 'graph-001'";
            Assert.AreEqual(1, await blockerCommand.ExecuteNonQueryAsync());
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await _store.SaveLayoutAsync(
            new AgentOrchestrationLayoutWriteRequest
            {
                ExpectedCurrentLayoutRevision = 0,
                Layout = new AgentOrchestrationGraphLayout
                {
                    GraphId = "graph-001",
                    BaseRevisionId = "graph-001/missing",
                    LayoutRevision = 1,
                    Nodes = []
                }
            },
            timeout.Token);

        Assert.AreEqual(AgentOrchestrationStoreStatus.NotFound, result.Status);
        Assert.AreEqual("orchestration.layout_base_revision_not_found", result.ErrorCode);
        await blockerTransaction.RollbackAsync();
    }

    [TestMethod]
    public async Task DiscoveryAndLayoutApis_ExposePagesAndKeepLayoutRouteIdentity()
    {
        await SaveDefinitionAsync();
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-api-list",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var readController = CreateApiController(_store, _signal);
        var graphAction = await readController.ListGraphs("default", limit: 10, offset: 0);
        var runAction = await readController.ListRuns(
            workspaceId: "default",
            graphId: "graph-001",
            status: AgentOrchestrationRunStatus.Draft,
            limit: 10,
            offset: 0);
        var graphPage = (AgentOrchestrationGraphPageDto)((JsonResult)graphAction.Result!).Value!;
        var runPage = (AgentOrchestrationRunPageDto)((JsonResult)runAction.Result!).Value!;

        Assert.HasCount(1, graphPage.Graphs);
        Assert.HasCount(1, runPage.Runs);
        Assert.AreEqual("run-api-list", runPage.Runs[0].RunId);

        var layoutController = new AgentOrchestrationLayoutApiController(_store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var layout = new AgentOrchestrationGraphLayout
        {
            GraphId = "graph-001",
            BaseRevisionId = "graph-001/r001",
            Nodes = [new AgentOrchestrationNodeLayout { NodeId = "root", X = 10, Y = 20 }]
        };
        var put = await layoutController.Put(
            "graph-001",
            new AgentOrchestrationLayoutWriteRequest
            {
                Layout = layout,
                ExpectedCurrentLayoutRevision = 0
            });
        var get = await layoutController.Get("graph-001", "graph-001/r001");
        var mismatch = await layoutController.Put(
            "other-graph",
            new AgentOrchestrationLayoutWriteRequest
            {
                Layout = layout with { LayoutRevision = 2 },
                ExpectedCurrentLayoutRevision = 1
            });

        Assert.IsInstanceOfType<JsonResult>(put.Result);
        Assert.IsInstanceOfType<JsonResult>(get.Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>(mismatch.Result);
    }

    [TestMethod]
    public async Task DeleteGraph_UsesHeadCas_CascadesEditorState_AndPreservesRunHistory()
    {
        await SaveDefinitionAsync();
        await _store.SaveLayoutAsync(new AgentOrchestrationLayoutWriteRequest
        {
            ExpectedCurrentLayoutRevision = 0,
            Layout = new AgentOrchestrationGraphLayout
            {
                GraphId = "graph-001",
                BaseRevisionId = "graph-001/r001",
                LayoutRevision = 1,
                Nodes = [new AgentOrchestrationNodeLayout { NodeId = "root", X = 10, Y = 20 }]
            }
        });

        var stale = await _store.DeleteGraphAsync(new AgentOrchestrationGraphDeleteRequest
        {
            GraphId = "graph-001",
            ExpectedCurrentRevision = 2
        });
        var deleted = await _store.DeleteGraphAsync(new AgentOrchestrationGraphDeleteRequest
        {
            GraphId = "graph-001",
            ExpectedCurrentRevision = 1
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.Conflict, stale.Status);
        Assert.AreEqual(1L, stale.CurrentVersion);
        Assert.AreEqual(AgentOrchestrationStoreStatus.Applied, deleted.Status);
        Assert.AreEqual(1, deleted.Value!.DeletedRevisionCount);
        Assert.AreEqual(1, deleted.Value.DeletedLayoutCount);
        Assert.IsNull(await _store.GetLatestRevisionAsync("graph-001"));
        Assert.IsNull(await _store.GetLayoutAsync("graph-001", "graph-001/r001"));

        await SaveDefinitionAsync();
        await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-preserved",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var blocked = await _store.DeleteGraphAsync(new AgentOrchestrationGraphDeleteRequest
        {
            GraphId = "graph-001",
            ExpectedCurrentRevision = 1
        });

        Assert.AreEqual(AgentOrchestrationStoreStatus.InvalidState, blocked.Status);
        Assert.AreEqual("orchestration.graph_has_runs", blocked.ErrorCode);
        Assert.IsNotNull(await _store.GetLatestRevisionAsync("graph-001"));
        Assert.IsNotNull(await _store.GetRunAsync("run-preserved"));
    }

    [TestMethod]
    public async Task ManagementApi_CreatesCompiledPlaceholderGraph_AndDeletesCurrentHead()
    {
        var controller = new AgentOrchestrationManagementApiController(_store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var created = await controller.CreateGraph(
            new AgentOrchestrationGraphCreateRequest
            {
                GraphId = "admin-graph",
                WorkspaceId = "default",
                RootSessionId = "admin-editor",
                Objective = "Draft an editable workflow",
                MaxConcurrency = 2
            });
        var createdResult = (JsonResult)created.Result!;
        var definition = (AgentOrchestrationGraphDefinition)createdResult.Value!;

        Assert.AreEqual(StatusCodes.Status201Created, createdResult.StatusCode);
        Assert.AreEqual("admin-graph/r001", definition.RevisionId);
        Assert.AreEqual(AgentOrchestrationNodeKind.HumanInput, definition.Nodes.Single().Kind);
        Assert.AreEqual(AgentOrchestrationComponentTypes.HumanInput, definition.Nodes.Single().Component.ComponentType);

        var deleted = await controller.DeleteGraph("admin-graph", expectedCurrentRevision: 1);
        var deletedResult = (JsonResult)deleted.Result!;
        Assert.AreEqual("admin-graph", ((AgentOrchestrationGraphDeleteReceipt)deletedResult.Value!).GraphId);
        Assert.IsNull(await _store.GetLatestRevisionAsync("admin-graph"));
    }

    private SqliteAgentOrchestrationStore CreateStore()
        => CreateStore(_signal);

    private SqliteAgentOrchestrationStore CreateStore(
        IAgentOrchestrationCommittedEventSignal eventSignal)
        => new(
            _dbFactory,
            new AgentOrchestrationGraphCompiler(),
            eventSignal,
            _timeProvider,
            NullLogger<SqliteAgentOrchestrationStore>.Instance);

    private static AgentOrchestrationApiController CreateApiController(
        IAgentOrchestrationQueryStore queryStore,
        IAgentOrchestrationCommittedEventSignal eventSignal)
    {
        var controller = new AgentOrchestrationApiController(
            AgentOrchestrationComponentRegistry.Default,
            queryStore,
            new AgentOrchestrationEventFollower(queryStore, eventSignal),
            NullLogger<AgentOrchestrationApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private async Task SaveDefinitionAsync()
    {
        var result = await _store.SaveRevisionAsync(new AgentOrchestrationRevisionWriteRequest
        {
            Definition = CreateDefinition(),
            ExpectedCurrentRevision = 0
        });
        Assert.IsTrue(result.Success, result.ErrorMessage);
    }

    private async Task CreateActiveRunAsync()
    {
        await SaveDefinitionAsync();
        var created = await _store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = "run-001",
            RevisionId = "graph-001/r001",
            RequestedByAgentId = "main-agent"
        });
        var activated = await _store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
        {
            RunId = "run-001",
            ExpectedVersion = created.Value!.Version
        });
        Assert.IsTrue(activated.Success, activated.ErrorMessage);
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class RecordingSignal : IAgentOrchestrationCommittedEventSignal
    {
        public List<long> Heads { get; } = [];
        public ValueTask WaitForChangeAsync(string runId, long knownHead, CancellationToken ct)
            => ValueTask.CompletedTask;
        public void Signal(string runId, long committedThroughSequence)
            => Heads.Add(committedThroughSequence);
    }
}
