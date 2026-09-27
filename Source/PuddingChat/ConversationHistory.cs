namespace PuddingChat;

/// <summary>Stable ordering for rows that share the same millisecond timestamp.</summary>
public sealed record HistoryCursor(long CreatedAt, long RowId) : IComparable<HistoryCursor>
{
    public int CompareTo(HistoryCursor? other) => other is null ? 1
        : CreatedAt != other.CreatedAt ? CreatedAt.CompareTo(other.CreatedAt) : RowId.CompareTo(other.RowId);
}
public sealed record HistoryPage(string MainSessionId, HistoryCursor Before, HistoryCursor? Next, ChatMessage[] Messages);
public interface IConversationHistory
{
    Task<HistoryPage> ReadHistoryAsync(RoleKey role, string sessionId, HistoryCursor before, CancellationToken ct);
}
