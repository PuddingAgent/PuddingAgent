namespace PuddingChat;

/// <summary>Small per-role bookmark; never retains message payloads or native controls.</summary>
public sealed record ReadingBookmark(string MainSessionId, ReadingPosition Position, DateTimeOffset? MessageCreatedAt)
{
    public ReadingPosition PositionFor(Conversation conversation) => conversation.MainSessionId == MainSessionId
        ? Position : ReadingPosition.Latest;

    public bool NeedsHistory(Conversation conversation) => conversation.MainSessionId == MainSessionId
        && !Position.FollowLatest && Position.MessageId is { } id && MessageCreatedAt is { } created
        && !conversation.Messages.Any(m => m.MessageId == id)
        && conversation.OlderCursor is { } cursor && cursor.CreatedAt >= created.ToUnixTimeMilliseconds();
}
