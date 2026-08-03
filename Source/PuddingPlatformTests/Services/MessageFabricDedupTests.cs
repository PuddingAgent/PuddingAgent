using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services;

[TestClass]
public class MessageFabricDedupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PlatformDbContext _db;
    private readonly MessageFabricStore _store;
    private readonly MessageSystem _system;
    private readonly FakeEventBus _eventBus;

    public MessageFabricDedupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new PlatformDbContext(options);
        _db.Database.EnsureCreated();
        _store = new MessageFabricStore(_db, NullLogger<MessageFabricStore>.Instance);
        _eventBus = new FakeEventBus();
        _system = new MessageSystem(
            new FakeRouter(),
            _store,
            _eventBus,
            new PuddingPlatform.Services.MessageFabric.WorkspaceRoomParticipantProvider(
                NullLogger<PuddingPlatform.Services.MessageFabric.WorkspaceRoomParticipantProvider>.Instance),
            NullLogger<MessageSystem>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [TestMethod]
    public async Task SendAsync_FirstCall_ReturnsDeliveryIds()
    {
        var env = CreateEnvelope("msg-first");
        FakeRouter.NextEnvelope = env;
        var result = await _system.SendAsync(env);
        Assert.IsTrue(result.DeliveryIds.Count > 0);
        Assert.AreEqual(1, await _db.RoomMessages.CountAsync(m => m.MessageId == env.MessageId));
    }

    [TestMethod]
    public async Task SendAsync_DuplicateCall_EmptyDeliveryIds_NoExtraRoomMessage()
    {
        var env = CreateEnvelope("msg-dup-send");
        FakeRouter.NextEnvelope = env;
        var r1 = await _system.SendAsync(env);
        Assert.IsTrue(r1.DeliveryIds.Count > 0);

        FakeRouter.NextEnvelope = env;
        var r2 = await _system.SendAsync(env);
        Assert.AreEqual(0, r2.DeliveryIds.Count);

        Assert.AreEqual(1, await _db.RoomMessages.CountAsync(m => m.MessageId == env.MessageId));
    }

    [TestMethod]
    public async Task SendAsync_DuplicateCall_NoEventsPublished()
    {
        var env = CreateEnvelope("msg-dup-events");
        FakeRouter.NextEnvelope = env;
        await _system.SendAsync(env);
        Assert.IsTrue(_eventBus.Published.Count > 0, "First call should publish");

        _eventBus.Published.Clear();
        FakeRouter.NextEnvelope = env;
        await _system.SendAsync(env);
        Assert.AreEqual(0, _eventBus.Published.Count, "Duplicate should not publish");
    }

    [TestMethod]
    public async Task UniqueIndex_DuplicateCall_Safe()
    {
        var env = CreateEnvelope("msg-unique-idx");
        FakeRouter.NextEnvelope = env;
        var r1 = await _system.SendAsync(env);
        Assert.IsTrue(r1.DeliveryIds.Count > 0);
        var r2 = await _system.SendAsync(env);
        Assert.AreEqual(0, r2.DeliveryIds.Count);
    }

    private static MessageEnvelope CreateEnvelope(string messageId) => new()
    {
        MessageId = messageId,
        RoomId = "test-room",
        Content = "hello",
        From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "u1", WorkspaceId = "default", DisplayName = "U" },
        To = [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "a1", WorkspaceId = "default" }],
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ConversationId = null, ReplyToMessageId = null, CorrelationId = null, CausationId = null,
        Audience = "direct", Visibility = "public",
        Metadata = new Dictionary<string, string>(),
        Priority = 0,
    };

    // ── Fakes ─────────────────────────────────────────────────────

    private sealed class FakeRouter : IMessageRouter
    {
        public static MessageEnvelope? NextEnvelope;
        private static MessageRoutePlan MakePlan(MessageEnvelope env) => new()
        {
            MessageId = env.MessageId,
            RoomMessage = new RoomMessage
            {
                MessageId = env.MessageId, RoomId = env.RoomId!, From = env.From,
                Audience = env.Audience, Visibility = env.Visibility, Content = env.Content,
                ConversationId = env.ConversationId, ReplyToMessageId = env.ReplyToMessageId,
                CorrelationId = env.CorrelationId, CausationId = env.CausationId,
                Metadata = env.Metadata, CreatedAt = env.CreatedAt,
            },
            Deliveries =
            [
                new MessageDelivery { DeliveryId = Guid.NewGuid().ToString(), MessageId = env.MessageId,
                    Target = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "u1", WorkspaceId = "default" }, Priority = 0 },
                new MessageDelivery { DeliveryId = Guid.NewGuid().ToString(), MessageId = env.MessageId,
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "a1", WorkspaceId = "default" }, Priority = 0 }
            ]
        };
        public Task<MessageRoutePlan> RouteAsync(MessageEnvelope env, IReadOnlyList<RoomParticipant> participants, CancellationToken ct)
            => Task.FromResult(MakePlan(NextEnvelope ?? env));
    }

    private sealed class FakeEventBus : IInternalEventBus
    {
        public List<InternalEvent> Published { get; } = [];
        public Task PublishAsync(InternalEvent evt, CancellationToken ct) { Published.Add(evt); return Task.CompletedTask; }
        public Task<IEventSubscriptionHandle> SubscribeAsync(string eventTypePattern, Func<InternalEvent, Task> handler, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => throw new NotImplementedException();
    }
}
