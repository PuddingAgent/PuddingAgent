using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingCode.Services;
using PuddingPlatform.Data;
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
    public async Task GetAgentQueueAsync_ExcludesTerminalByDefault_AndCanIncludeTerminal()
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

        CollectionAssert.AreEqual(new[] { "d-active" }, activeOnly.Items.Select(item => item.DeliveryId).ToArray());
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
