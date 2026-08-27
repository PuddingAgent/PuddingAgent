using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Conversation;

namespace PuddingPlatformTests.Services.Conversation;

[TestClass]
public sealed class ConversationNotificationStoreTests
{
    [TestMethod]
    public async Task AcceptAsync_AppendsMessageEventWithoutCreatingTurn_AndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var signal = new RecordingSignal();
        var store = new ConversationNotificationStore(
            db,
            signal,
            NullLogger<ConversationNotificationStore>.Instance);
        var request = new ConversationNotificationRequest(
            WorkspaceId: "default",
            ConversationId: "agent-b-main",
            AgentInstanceId: "agent-b",
            MessageId: "fabric-notification:1234",
            Content: "passive result",
            CreatedAt: 100,
            UserId: "fabric:agent-a",
            Metadata: new Dictionary<string, string>
            {
                ["intent"] = "agent_reply",
                ["requires_response"] = "false",
            },
            CorrelationId: "corr-1",
            CausationId: "cause-1");

        var first = await store.AcceptAsync(request);
        var replay = await store.AcceptAsync(request);

        Assert.IsFalse(first.AlreadyAccepted);
        Assert.IsTrue(replay.AlreadyAccepted);
        Assert.AreEqual(first.AcceptedSequence, replay.AcceptedSequence);
        Assert.AreEqual(1, await db.ChatMessages.CountAsync());
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
        var message = await db.ChatMessages.SingleAsync();
        Assert.AreEqual("user", message.Role);
        Assert.AreEqual("passive result", message.Content);
        var evt = await db.ConversationEvents.SingleAsync();
        Assert.AreEqual(ConversationEventTypes.MessageCreated, evt.Type);
        Assert.AreEqual("message.fabric.notification", evt.ProducerComponent);
        Assert.AreEqual("corr-1", evt.CorrelationId);
        Assert.AreEqual("cause-1", evt.CausationId);
        Assert.AreEqual(first.AcceptedSequence, signal.LastSequence);
    }

    private sealed class RecordingSignal : ICommittedEventSignal
    {
        public long LastSequence { get; private set; }

        public ValueTask WaitForChangeAsync(
            string conversationId,
            long knownHead,
            CancellationToken ct)
            => ValueTask.CompletedTask;

        public void Signal(string conversationId, long committedThroughSequence)
            => LastSequence = committedThroughSequence;
    }
}
