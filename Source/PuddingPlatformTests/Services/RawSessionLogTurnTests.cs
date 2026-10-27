using PuddingCode.Abstractions;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

public sealed partial class RawSessionLogServiceTests
{
    [TestMethod]
    public async Task LargeEventOnlyTurn_ReportsIncompleteTranscript_AndCanBeReadWithTurnCursor()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.ConversationEvents.AddRange(Enumerable.Range(1, 10001).Select(i =>
            BuildEvent("ws-a", "agent-a", "session-a", i, "message.content.appended", "{\"delta\":\"part\"}", "2026-06-02T08:00:00Z")));
        var foreign = BuildEvent("ws-a", "agent-a", "session-a", 10002, "message.content.appended", "{\"delta\":\"foreign\"}", "2026-06-02T08:00:01Z");
        foreign.TurnId = "other-turn";
        scope.Db.ConversationEvents.Add(foreign);
        await scope.Db.SaveChangesAsync();
        var service = new RawSessionLogService(scope.Factory);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.ReadMessagesAsync("ws-a", "session-a", turnId: "turn-1"));
        StringAssert.Contains(error.Message, "read_raw_events");
        var page = await service.ReadSessionAsync("ws-a", "session-a", afterSequence: 9998, limit: 2, turnId: "turn-1");
        Assert.AreEqual(2, page.Events.Count);
        Assert.IsTrue(page.HasMore);
        Assert.AreEqual(10000L, page.NextSequence);
        var tail = await service.ReadSessionAsync("ws-a", "session-a", afterSequence: page.NextSequence, turnId: "turn-1");
        Assert.AreEqual(1, tail.Events.Count);
        Assert.IsFalse(tail.HasMore);
        Assert.AreEqual(10001L, tail.Events[0].SequenceNum);
    }

    [TestMethod]
    public async Task TurnLookup_FiltersBeforePagination_AndKeepsWorkspaceSessionAgentFences()
    {
        await using var scope = await CreateScopeAsync();
        await SeedEventAsync(scope.Db, "ws-a", "agent-a", "session-a", 1, "turn.completed", "{}", "2026-06-02T08:00:00Z");
        scope.Db.ChatMessages.AddRange(Enumerable.Range(1, 1100).Select(i => new ChatMessageEntity
        {
            MessageId = $"m{i}", WorkspaceId = "ws-a", SessionId = "session-a", AgentInstanceId = "agent-a",
            TurnId = $"turn-{i}", Role = "user", Content = $"original {i}", CreatedAt = i,
        }));
        scope.Db.ChatMessages.Add(new ChatMessageEntity
        {
            MessageId = "foreign-agent", WorkspaceId = "ws-a", SessionId = "session-a", AgentInstanceId = "agent-b",
            TurnId = "turn-1", Role = "agent", Content = "must not appear", CreatedAt = 2000,
        });
        await scope.Db.SaveChangesAsync();
        var service = new RawSessionLogService(scope.Factory);
        var page = await service.ReadMessagesAsync("ws-a", "session-a", "agent-a", limit: 1, turnId: "turn-1");
        Assert.AreEqual(1, page.Messages.Count);
        Assert.AreEqual("original 1", page.Messages[0].Content);
        Assert.AreEqual("turn-1", page.Messages[0].TurnId);
        Assert.IsFalse(page.HasMore);
        Assert.AreEqual(0, (await service.ReadMessagesAsync("ws-b", "session-a", turnId: "turn-1")).Messages.Count);
        Assert.AreEqual(0, (await service.ReadMessagesAsync("ws-a", "session-b", turnId: "turn-1")).Messages.Count);
        Assert.AreEqual(0, (await service.ReadMessagesAsync("ws-a", "session-a", turnId: "absent")).Messages.Count);
        var search = await service.GrepMessagesAsync(new RawSessionLogSearchRequest
        { WorkspaceId = "ws-a", SessionId = "session-a", AgentInstanceId = "agent-a", Query = "original 1" });
        Assert.IsTrue(search.Matches.All(m => m.TurnId is not null));
    }

    [TestMethod]
    public async Task TurnLookup_EventFallback_ReturnsOriginalTurnIdentity()
    {
        await using var scope = await CreateScopeAsync();
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 1, "message.created", "{\"role\":\"user\",\"content\":\"original request\"}", "2026-06-02T08:00:00Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 2, "message.content.appended", "{\"delta\":\"original reply\"}", "2026-06-02T08:00:01Z");
        var service = new RawSessionLogService(scope.Factory);
        var page = await service.ReadMessagesAsync("ws-a", "session-a", turnId: "turn-1");
        Assert.AreEqual(2, page.Messages.Count);
        Assert.IsTrue(page.Messages.All(m => m.TurnId == "turn-1"));
        Assert.AreEqual(0, (await service.ReadMessagesAsync("ws-a", "session-a", turnId: "missing")).Messages.Count);
    }
}
