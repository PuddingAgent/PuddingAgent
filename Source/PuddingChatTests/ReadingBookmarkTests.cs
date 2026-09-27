using PuddingChat;

namespace PuddingChatTests;

public class ReadingBookmarkTests
{
    private static Conversation Snapshot(HistoryCursor? cursor = null) => new("w", "a", "s", [], null, 10, cursor);
    private static ReadingBookmark Bookmark() => new("s", new("old", 12, 500, false), DateTimeOffset.FromUnixTimeMilliseconds(100));

    [Fact]
    public void ReadsOnlyUntilAnchorIsFoundOrItsTimeRangeHasBeenPassed()
    {
        var bookmark = Bookmark();
        Assert.True(bookmark.NeedsHistory(Snapshot(new(200, 20))));
        Assert.True(bookmark.NeedsHistory(Snapshot(new(100, 10)))); // Same-millisecond rows still need paging.
        Assert.False(bookmark.NeedsHistory(Snapshot(new(99, 9))));
        Assert.False(bookmark.NeedsHistory(Snapshot()));
        Assert.False(bookmark.NeedsHistory(Snapshot(new(200, 20)) with { Messages =
            [new("old", null, "user", "user", DateTimeOffset.UnixEpoch, "", "done", [])] }));
    }

    [Fact]
    public void RotatedSessionAndLiveAnchorsDoNotSearchUnrelatedHistory()
    {
        var bookmark = Bookmark(); var rotated = Snapshot(new(200, 20)) with { MainSessionId = "new-session" };
        Assert.False(bookmark.NeedsHistory(rotated));
        Assert.Equal(ReadingPosition.Latest, bookmark.PositionFor(rotated));
        Assert.Equal(bookmark.Position, bookmark.PositionFor(Snapshot()));
        Assert.False((bookmark with { MessageCreatedAt = null }).NeedsHistory(Snapshot(new(200, 20))));
        Assert.False((bookmark with { Position = ReadingPosition.Latest }).NeedsHistory(Snapshot(new(200, 20))));
    }
}
