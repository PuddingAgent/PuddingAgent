using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class MessageQueueProjectionServiceTests
{
    [TestMethod]
    public async Task GetAgentQueueAsync_ReturnsActiveDeliveriesOrderedByPriorityThenCreatedAt()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan("m-low", "d-low", "room-default", "low", priority: 0, createdAt: 100), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-high-new", "d-high-new", "room-default", "high-new", priority: 10, createdAt: 300), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-high-old", "d-high-old", "room-default", "high-old", priority: 10, createdAt: 200), CancellationToken.None);
        await SetDeliveryCreatedAtAsync(db, "d-low", 100);
        await SetDeliveryCreatedAtAsync(db, "d-high-new", 300);
        await SetDeliveryCreatedAtAsync(db, "d-high-old", 200);

        var service = new MessageQueueProjectionService(db);
        var snapshot = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
        }, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "d-high-old", "d-high-new", "d-low" },
            snapshot.Items.Select(item => item.DeliveryId).ToArray());
        Assert.AreEqual("high-old", snapshot.Items[0].Content);
        Assert.AreEqual(10, snapshot.Items[0].Priority);
    }

    [TestMethod]
    public async Task GetAgentQueueAsync_DefaultContainsOnlyUnclaimedDeliveries_AndDiagnosticsCanIncludeAll()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan("m-active", "d-active", "room-default", "active", priority: 0, createdAt: 100), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-done", "d-done", "room-default", "done", priority: 20, createdAt: 50), CancellationToken.None);

        var delivered = await db.MessageDeliveries.SingleAsync(item => item.DeliveryId == "d-done");
        delivered.Status = MessageDeliveryStatuses.Delivered;
        delivered.AckAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var claimed = await db.MessageDeliveries.SingleAsync(item => item.DeliveryId == "d-active");
        claimed.Status = MessageDeliveryStatuses.Delivering;
        claimed.ClaimedByExecutionId = "execution-1";
        await db.SaveChangesAsync();

        var service = new MessageQueueProjectionService(db);
        var activeOnly = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
        }, CancellationToken.None);
        var withTerminal = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            IncludeTerminal = true,
        }, CancellationToken.None);

        Assert.IsEmpty(activeOnly.Items);
        CollectionAssert.AreEqual(new[] { "d-done", "d-active" }, withTerminal.Items.Select(item => item.DeliveryId).ToArray());
    }

    [TestMethod]
    public async Task GetAgentQueueAsync_FiltersByRoomAndAgent()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan("m-a", "d-a", "room-a", "room-a message", priority: 5, createdAt: 100), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-b", "d-b", "room-b", "room-b message", priority: 10, createdAt: 50), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-other", "d-other", "room-a", "other agent message", priority: 20, createdAt: 10, targetId: "consultant"), CancellationToken.None);

        var service = new MessageQueueProjectionService(db);
        var snapshot = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            RoomId = "room-a",
        }, CancellationToken.None);

        Assert.AreEqual("default", snapshot.WorkspaceId);
        Assert.AreEqual("assistant", snapshot.AgentId);
        Assert.AreEqual("room-a", snapshot.RoomId);
        CollectionAssert.AreEqual(new[] { "d-a" }, snapshot.Items.Select(item => item.DeliveryId).ToArray());
    }

    [TestMethod]
    public async Task GetAgentQueueAsync_ProjectsDurableChatTurnsWithoutBrowserOwnedQueue()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.ChatMessages.AddRange(
            ChatMessage("user-pending", "session-a", "queued after busy", 100),
            ChatMessage("user-running", "session-a", "already running", 200),
            ChatMessage("user-done", "session-a", "already done", 300));
        db.ChatExecutionCommands.AddRange(
            ChatCommand("cmd-pending", "user-pending", "session-a", "pending", 100),
            ChatCommand("cmd-running", "user-running", "session-a", "running", 200, runId: "run-1"),
            ChatCommand("cmd-done", "user-done", "session-a", "succeeded", 300));
        await db.SaveChangesAsync();

        var service = new MessageQueueProjectionService(db);
        var active = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            RoomId = "session-a",
        }, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "turn:cmd-pending" },
            active.Items.Select(item => item.DeliveryId).ToArray());
        Assert.IsTrue(active.Items.All(item => item.QueueKind == MessageQueueKinds.ChatTurn));
        Assert.AreEqual("queued after busy", active.Items[0].Content);
        Assert.AreEqual(MessageDeliveryStatuses.Queued, active.Items[0].Status);
        Assert.AreEqual("pending", active.Items[0].ExecutionState);
        Assert.IsFalse(active.Items.Any(item => item.DeliveryId == "turn:cmd-running"));

        var withTerminal = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            RoomId = "session-a",
            IncludeTerminal = true,
        }, CancellationToken.None);

        var completed = withTerminal.Items.Single(item => item.DeliveryId == "turn:cmd-done");
        var running = withTerminal.Items.Single(item => item.DeliveryId == "turn:cmd-running");
        Assert.AreEqual(MessageDeliveryStatuses.Delivering, running.Status);
        Assert.AreEqual("run-1", running.ClaimedByExecutionId);
        Assert.AreEqual(MessageDeliveryStatuses.Delivered, completed.Status);
        Assert.AreEqual("delivered", completed.Substate);
    }

    [TestMethod]
    public async Task GetAgentQueueAsync_HidesSystemDeliveriesAndProjectsEnvelopeContext()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync(
            "default",
            RoutePlan(
                "m-public",
                "d-public",
                "room-default",
                "visible user message",
                priority: 0,
                createdAt: 100),
            CancellationToken.None);
        await store.PersistRouteAsync(
            "default",
            RoutePlan(
                "m-system",
                "d-system",
                "room-default",
                AgentContextEnvelopeRenderer.RenderForAgent(new AgentContextEnvelope
                {
                    MessageId = "m-system",
                    MessageType = "subagent_result",
                    ContentType = "text/plain",
                    CreatedAt = 200,
                    WorkspaceId = "default",
                    RoomId = "room-default",
                    From = new AgentContextEndpoint("agent", "child", "Child"),
                    To = [new AgentContextEndpoint("agent", "assistant", "Assistant")],
                    Constraints = [],
                    Context = new AgentContextPayload("text/plain", "child failed"),
                }),
                priority: 10,
                createdAt: 200,
                visibility: MessageVisibilities.System),
            CancellationToken.None);

        var service = new MessageQueueProjectionService(db);
        var userQueue = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
        }, CancellationToken.None);
        var diagnosticQueue = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            IncludeSystem = true,
        }, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "d-public" },
            userQueue.Items.Select(item => item.DeliveryId).ToArray());
        Assert.HasCount(2, diagnosticQueue.Items);
        var systemItem = diagnosticQueue.Items.Single(item => item.DeliveryId == "d-system");
        Assert.AreEqual("child failed", systemItem.Content);
        Assert.AreEqual(MessageVisibilities.System, systemItem.Visibility);
        Assert.AreEqual("subagent_result", systemItem.MessageType);
        Assert.AreEqual("text/plain", systemItem.ContentType);
    }

    [TestMethod]
    public async Task GetAgentQueueAsync_ProjectsSubstateDeferCountExecutionStateAndPosition()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);

        await store.PersistRouteAsync("default", RoutePlan("m-fresh", "d-fresh", "room-default", "fresh", priority: 0, createdAt: 100), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-waiting", "d-waiting", "room-default", "waiting", priority: 0, createdAt: 200), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-retry", "d-retry", "room-default", "retry", priority: 0, createdAt: 300), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-done", "d-done", "room-default", "done", priority: 0, createdAt: 400), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-dead", "d-dead", "room-default", "dead", priority: 0, createdAt: 500), CancellationToken.None);
        await store.PersistRouteAsync("default", RoutePlan("m-delivering", "d-delivering", "room-default", "delivering", priority: 0, createdAt: 600), CancellationToken.None);

        // queued + deferCount > 0 -> substate "waiting"（busy 挂起）
        var waiting = await db.MessageDeliveries.SingleAsync(d => d.DeliveryId == "d-waiting");
        waiting.DeferCount = 2;
        waiting.AvailableAt = 300;
        waiting.ExecutionState = "Busy";
        // retrying（真实失败退避，lastError 不含 busy -> ExecutionState null）
        var retry = await db.MessageDeliveries.SingleAsync(d => d.DeliveryId == "d-retry");
        retry.Status = MessageDeliveryStatuses.Retrying;
        retry.AvailableAt = 100;
        retry.LastError = "upstream 503";
        // delivered 终态
        var done = await db.MessageDeliveries.SingleAsync(d => d.DeliveryId == "d-done");
        done.Status = MessageDeliveryStatuses.Delivered;
        done.AvailableAt = 200;
        done.AckAt = 600;
        // dead_letter 终态（lastError 含 busy -> ExecutionState "Busy"）
        var dead = await db.MessageDeliveries.SingleAsync(d => d.DeliveryId == "d-dead");
        dead.Status = MessageDeliveryStatuses.DeadLetter;
        dead.DeferCount = 3;
        dead.AvailableAt = 700;
        dead.LastError = "{\"executionState\":\"Busy\",\"message\":\"Agent busy\"}";
        dead.ExecutionState = "Busy";
        // delivering（锁定映射之外 -> substate 身份回退）
        var delivering = await db.MessageDeliveries.SingleAsync(d => d.DeliveryId == "d-delivering");
        delivering.Status = MessageDeliveryStatuses.Delivering;
        delivering.AvailableAt = 400;
        await db.SaveChangesAsync();

        var service = new MessageQueueProjectionService(db);
        var snapshot = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            IncludeTerminal = true,
        }, CancellationToken.None);

        var byId = snapshot.Items.ToDictionary(i => i.DeliveryId);

        // substate 锁定映射
        Assert.AreEqual("fresh", byId["d-fresh"].Substate);
        Assert.AreEqual("waiting", byId["d-waiting"].Substate);
        Assert.AreEqual("retrying", byId["d-retry"].Substate);
        Assert.AreEqual("delivered", byId["d-done"].Substate);
        Assert.AreEqual("dead_letter", byId["d-dead"].Substate);
        Assert.AreEqual("delivering", byId["d-delivering"].Substate);

        // deferCount / executionState 透出
        Assert.AreEqual(0, byId["d-fresh"].DeferCount);
        Assert.AreEqual(2, byId["d-waiting"].DeferCount);
        Assert.AreEqual(3, byId["d-dead"].DeferCount);
        Assert.IsNull(byId["d-fresh"].ExecutionState);
        Assert.IsNull(byId["d-retry"].ExecutionState);
        Assert.AreEqual("Busy", byId["d-waiting"].ExecutionState);
        Assert.AreEqual("Busy", byId["d-dead"].ExecutionState);

        // position：队列内序号按 availableAt 升序（null=可立即处理排最前）
        // d-fresh(null) < d-retry(100) < d-done(200) < d-waiting(300) < d-delivering(400) < d-dead(700)
        Assert.AreEqual(0, byId["d-fresh"].Position);
        Assert.AreEqual(1, byId["d-retry"].Position);
        Assert.AreEqual(2, byId["d-done"].Position);
        Assert.AreEqual(3, byId["d-waiting"].Position);
        Assert.AreEqual(4, byId["d-delivering"].Position);
        Assert.AreEqual(5, byId["d-dead"].Position);
    }

    [TestMethod]
    public async Task GetAgentQueueAsync_PositionIsComputedOverFullQueue_BeyondReturnedLimit()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);

        // 6 queued rows with ascending availableAt; the projection limit is 3.
        for (var i = 0; i < 6; i++)
        {
            await store.PersistRouteAsync("default", RoutePlan($"m-{i}", $"d-{i}", "room-default", $"content-{i}", priority: 0, createdAt: i), CancellationToken.None);
        }

        var rows = await db.MessageDeliveries.ToListAsync();
        for (var i = 0; i < rows.Count; i++)
            rows[i].AvailableAt = i * 100;
        await db.SaveChangesAsync();

        var service = new MessageQueueProjectionService(db);
        var snapshot = await service.GetAgentQueueAsync(new MessageQueueProjectionQuery
        {
            WorkspaceId = "default",
            AgentId = "assistant",
            Limit = 3,
        }, CancellationToken.None);

        Assert.AreEqual(3, snapshot.Items.Count);
        // 每个返回项携带真实队列序号（按 availableAt 升序，含窗口之外的行）
        Assert.AreEqual(0, snapshot.Items.Single(i => i.DeliveryId == "d-0").Position);
        Assert.AreEqual(1, snapshot.Items.Single(i => i.DeliveryId == "d-1").Position);
        Assert.AreEqual(2, snapshot.Items.Single(i => i.DeliveryId == "d-2").Position);
    }
    private static DbContextOptions<PlatformDbContext> CreateOptions(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        return new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    private static async Task SetDeliveryCreatedAtAsync(
        PlatformDbContext db,
        string deliveryId,
        long createdAt)
    {
        var delivery = await db.MessageDeliveries.SingleAsync(item => item.DeliveryId == deliveryId);
        delivery.CreatedAt = createdAt;
        delivery.UpdatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private static ChatMessageEntity ChatMessage(
        string messageId,
        string sessionId,
        string content,
        long createdAt) => new()
    {
        MessageId = messageId,
        SessionId = sessionId,
        WorkspaceId = "default",
        AgentInstanceId = "assistant",
        Role = "user",
        Content = content,
        CreatedAt = createdAt,
    };

    private static ChatExecutionCommandEntity ChatCommand(
        string commandId,
        string userMessageId,
        string sessionId,
        string status,
        long createdAt,
        string? runId = null) => new()
    {
        CommandId = commandId,
        BatchId = $"batch-{commandId}",
        WorkspaceId = "default",
        SessionId = sessionId,
        MessageId = $"response-{commandId}",
        UserMessageId = userMessageId,
        TurnId = $"turn-{commandId}",
        AgentInstanceId = "assistant",
        UserId = "owner",
        Status = status,
        RunId = runId,
        CreatedAt = createdAt,
    };

    private static MessageRoutePlan RoutePlan(
        string messageId,
        string deliveryId,
        string roomId,
        string content,
        int priority,
        long createdAt,
        string targetId = "assistant",
        string visibility = MessageVisibilities.Public) => new()
    {
        MessageId = messageId,
        RoomMessage = new RoomMessageDraft
        {
            RoomId = roomId,
            MessageId = messageId,
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.User,
                Id = "owner",
                WorkspaceId = "default",
                DisplayName = "Owner",
            },
            Audience = MessageAudiences.Direct,
            Visibility = visibility,
            Content = content,
            CreatedAt = createdAt,
        },
        Deliveries =
        [
            new MessageDeliveryDraft
            {
                DeliveryId = deliveryId,
                MessageId = messageId,
                Target = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = targetId,
                    WorkspaceId = "default",
                    DisplayName = targetId,
                },
                Priority = priority,
            },
        ],
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
