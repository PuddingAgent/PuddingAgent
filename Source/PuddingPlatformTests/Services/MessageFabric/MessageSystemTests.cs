using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class MessageSystemTests
{
    [TestMethod]
    public async Task SendAsync_Broadcast_PersistsDeliveries_AndPublishesMessageDeliverEvents()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var bus = new RecordingInternalEventBus();
        var system = new MessageSystem(
            new MessageRouter(),
            new MessageFabricStore(db),
            bus,
            new WorkspaceRoomParticipantProvider(new RecordingWorkspaceAgentCatalog(
                Agent("assistant"),
                Agent("consultant"),
                Agent("frozen", isFrozen: true))));

        var result = await system.SendAsync(new MessageEnvelope
        {
            MessageId = "m-broadcast",
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.User,
                Id = "owner",
                WorkspaceId = "default",
            },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Room, Id = "room-default" }],
            RoomId = "room-default",
            Audience = MessageAudiences.Broadcast,
            Visibility = MessageVisibilities.Public,
            Content = "hello all",
            Priority = 5,
        });

        Assert.AreEqual("m-broadcast", result.MessageId);
        Assert.AreEqual(2, result.DeliveryIds.Count);
        Assert.AreEqual(1, await db.RoomMessages.CountAsync());
        Assert.AreEqual(2, await db.MessageDeliveries.CountAsync());
        Assert.AreEqual(2, bus.Published.Count);
        Assert.IsTrue(bus.Published.All(evt => evt.Type == "message.deliver"));
        Assert.IsTrue(bus.Published.All(evt => evt.Priority == EventPriorityLevel.Important));
        CollectionAssert.AreEqual(
            new[] { "assistant", "consultant" },
            bus.Published.Select(evt => evt.AgentId).OrderBy(id => id).ToArray());
        Assert.IsTrue(bus.Published.All(evt => evt.Payload is MessageDeliverEventPayload));
    }

    [TestMethod]
    public async Task SendAsync_DirectToUser_PublishesDeliveryWithoutAgentId()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bus = new RecordingInternalEventBus();
        var system = new MessageSystem(
            new MessageRouter(),
            new MessageFabricStore(db),
            bus,
            new WorkspaceRoomParticipantProvider(new RecordingWorkspaceAgentCatalog()));

        await system.SendAsync(new MessageEnvelope
        {
            MessageId = "m-direct",
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = "assistant",
                WorkspaceId = "default",
            },
            To = [new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" }],
            RoomId = "room-default",
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Private,
            Content = "please confirm",
        });

        Assert.AreEqual(1, bus.Published.Count);
        Assert.IsNull(bus.Published[0].AgentId);
        var payload = (MessageDeliverEventPayload)bus.Published[0].Payload!;
        Assert.AreEqual(MessageEndpointKinds.User, payload.Target.Kind);
        Assert.AreEqual("owner", payload.Target.Id);
    }

    [TestMethod]
    public async Task SendAsync_DirectToCatalogAgentShortAddress_PublishesCanonicalAgentId()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bus = new RecordingInternalEventBus();
        var system = new MessageSystem(
            new MessageRouter(),
            new MessageFabricStore(db),
            bus,
            new WorkspaceRoomParticipantProvider(new RecordingWorkspaceAgentCatalog(
                Agent("default.global_general-assistant.40a", displayName: "开发助手"))));

        await system.SendAsync(new MessageEnvelope
        {
            MessageId = "m-catalog-agent",
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.User,
                Id = "owner",
                WorkspaceId = "default",
            },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "general-assistant.40a" }],
            RoomId = "default",
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Private,
            Content = "ping",
        });

        Assert.AreEqual(1, bus.Published.Count);
        Assert.AreEqual("default.global_general-assistant.40a", bus.Published[0].AgentId);
        var payload = (MessageDeliverEventPayload)bus.Published[0].Payload!;
        Assert.AreEqual("default.global_general-assistant.40a", payload.Target.Id);
    }

    private static WorkspaceAgentDto Agent(
        string agentId,
        string? displayName = null,
        string? mainSessionId = null,
        bool isFrozen = false) => new(
        AgentId: agentId,
        Name: agentId,
        Description: null,
        DisplayName: displayName ?? agentId,
        AvatarId: null,
        AvatarUrl: null,
        SourceTemplateId: "global:general-assistant",
        MainSessionId: mainSessionId ?? $"{agentId}-main",
        SystemPromptOverride: null,
        PreferredProviderId: null,
        PreferredModelId: null,
        IsEnabled: true,
        IsFrozen: isFrozen,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static DbContextOptions<PlatformDbContext> CreateOptions(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        return new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    private sealed class RecordingWorkspaceAgentCatalog(params WorkspaceAgentDto[] agents) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>(agents);
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public List<InternalEvent> Published { get; } = [];

        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default) =>
            Task.FromResult<IEventSubscriptionHandle>(new RecordingSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = Guid.NewGuid().ToString("N");
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;

        public void Dispose()
        {
            IsActive = false;
        }
    }

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
