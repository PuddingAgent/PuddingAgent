namespace PuddingChat;

public sealed record MessageBounds(string Id, double Top, double Height);
public sealed record ReadingPosition(string? MessageId, double WithinMessage, double Offset, bool FollowLatest)
{
    public static ReadingPosition Latest { get; } = new(null, 0, 0, true);
    public static ReadingPosition Capture(IEnumerable<MessageBounds> messages, double offset, double scrollableHeight, bool followWhenNearBottom = true)
    {
        if (followWhenNearBottom && scrollableHeight - offset < 80) return Latest;
        var anchor = messages.FirstOrDefault(m => m.Top + m.Height > offset);
        return new(anchor?.Id, anchor is null ? 0 : offset - anchor.Top, offset, false);
    }
    public double Restore(IEnumerable<MessageBounds> messages, double scrollableHeight)
    {
        if (FollowLatest) return scrollableHeight;
        var anchor = messages.FirstOrDefault(m => m.Id == MessageId);
        return Math.Clamp(anchor is null ? Offset : anchor.Top + WithinMessage, 0, Math.Max(0, scrollableHeight));
    }
}
