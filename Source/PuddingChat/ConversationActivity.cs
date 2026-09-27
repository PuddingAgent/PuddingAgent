namespace PuddingChat;

/// <summary>A fixed replay ceiling prevents a busy producer from extending bootstrap forever.</summary>
public sealed record ActivityRead(string MainSessionId, string RunId, string TurnId, long AfterSequence,
    long? ThroughSequence = null, bool Replay = false);
public sealed record ActivityPage(ActivityRead Read, long ThroughSequence, bool HasMore, bool RequiresSnapshot,
    ProcessItem[] Items);

public interface IConversationActivity
{
    Task<ActivityPage> ReadActivityAsync(RoleKey role, ActivityRead read, CancellationToken ct);
}

/// <summary>Presentation reducer only. Lifecycle transitions require Core's authoritative snapshot.</summary>
public static class ConversationActivity
{
    public static Conversation? Apply(Conversation current, ActivityPage page)
    {
        var read = page.Read;
        if (page.RequiresSnapshot || current.MainSessionId != read.MainSessionId
            || current.ActiveRun is not { } run || run.RunId != read.RunId
            || run.OutputSnapshot.Window?.TurnId != read.TurnId
            || current.EventCursor != read.AfterSequence || page.ThroughSequence < read.AfterSequence
            || (page.HasMore && page.ThroughSequence == read.AfterSequence)) return null;
        var items = run.OutputSnapshot.ProcessItems.Concat(page.Items)
            .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.Last()).OrderBy(i => i.Sequence).ToArray();
        return current with { EventCursor = page.ThroughSequence, ActiveRun = run with {
            OutputSnapshot = new(string.Concat(items.Where(i => i.Kind == "text").Select(i => i.Text)), items,
                new(read.TurnId, page.ThroughSequence, items.FirstOrDefault()?.Sequence ?? page.ThroughSequence,
                    items.LastOrDefault()?.Sequence ?? page.ThroughSequence, page.HasMore)) } };
    }
}
