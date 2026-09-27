using PuddingChat;

namespace PuddingChatTests;

public class ConversationHistoryTests
{
    private static ChatMessage Message(string id, int at, string text = "") =>
        new(id, null, "user", "user", DateTimeOffset.FromUnixTimeMilliseconds(at), text, "done", []);
    private static ChatSelection State()
    {
        var state = new ChatSelection(); state.Select(new("w", "a"));
        state.Apply(state.Generation, new("w", "a", "s", [Message("new", 20)], null, 10, new(20, 2)));
        return state;
    }
    [Fact]
    public void OlderPagesKeepLiveVersionAndSurviveSnapshotRefresh()
    {
        var state = State(); var generation = state.Generation;
        Assert.True(state.PrependHistory(generation, new("s", new(20, 2), new(10, 1), [Message("old", 10), Message("new", 20, "stale")])));
        Assert.Equal("", state.Conversation!.Messages.Single(m => m.MessageId == "new").Content);
        Assert.True(state.Apply(generation, new("w", "a", "s", [Message("new", 20, "updated"), Message("next", 30)], null, 11, new(20, 2))));
        Assert.Equal(new[] { "old", "new", "next" }, state.Conversation!.Messages.Select(m => m.MessageId));
        Assert.Equal(new HistoryCursor(10, 1), state.Conversation.OlderCursor);
        Assert.Equal(11, state.Conversation.EventCursor);
    }
    [Fact]
    public void LateForeignAndNonProgressingPagesAreRejected()
    {
        var state = State(); var page = new HistoryPage("s", new(20, 2), null, []);
        Assert.False(state.PrependHistory(state.Generation - 1, page));
        Assert.False(state.PrependHistory(state.Generation, page with { MainSessionId = "other" }));
        Assert.False(state.PrependHistory(state.Generation, page with { Before = new(20, 1) }));
        Assert.False(state.PrependHistory(state.Generation, page with { Next = new(20, 3) }));
        Assert.True(state.PrependHistory(state.Generation, page));
        Assert.Null(state.Conversation!.OlderCursor);
        state.Apply(state.Generation, new("w", "a", "rotated", [], null, 0));
        Assert.Empty(state.Conversation!.Messages);
    }
    [Fact]
    public void EnvelopeDuplicatesAcrossPagesKeepCurrentDisplayIdentity()
    {
        var state = State();
        state.Apply(state.Generation, state.Conversation! with { Messages = [Message("new-row", 20) with { CanonicalMessageId = "original" }] });
        state.PrependHistory(state.Generation, new("s", new(20, 2), null,
            [Message("old-row", 10) with { CanonicalMessageId = "original" }]));
        Assert.Equal("new-row", Assert.Single(state.Conversation!.Messages).MessageId);
    }
    [Fact]
    public void ExplicitHistoryLoadAnchorsEvenWhenPreviouslyNearBottom()
    {
        var reading = ReadingPosition.Capture([new("current", 0, 200)], 20, 40, followWhenNearBottom: false);
        Assert.False(reading.FollowLatest);
        Assert.Equal(320, reading.Restore([new("older", 0, 300), new("current", 300, 200)], 400));
    }
    [Fact]
    public void DisjointNewPageKeepsCursorForBackfillingTheGap()
    {
        var state = State();
        state.PrependHistory(state.Generation, new("s", new(20, 2), null, [Message("old", 10)]));
        state.Apply(state.Generation, new("w", "a", "s", [Message("burst", 100)], null, 100, new(100, 20)));
        Assert.Equal(3, state.Conversation!.Messages.Length);
        Assert.Equal(new HistoryCursor(100, 20), state.Conversation.OlderCursor);
    }
}
