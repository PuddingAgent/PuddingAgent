using PuddingChat;

namespace PuddingChatTests;

public class ConversationActivityTests
{
    private static Conversation Snapshot(long sequence = 0) => new("workspace", "role", "session", [],
        new("run", "running", "执行中", "", new("", [], new("turn", sequence, sequence, sequence, false))), sequence);
    private static ActivityPage Page(long after, long through, params ProcessItem[] items) =>
        new(new("session", "run", "turn", after), through, false, false, items);

    [Fact]
    public void PagesRecoverMoreThanSnapshotWindowWithoutDuplicatingText()
    {
        var first = Page(0, 80, Enumerable.Range(1, 80).Select(i => new ProcessItem($"e{i}", "thinking", "running", "reason", i)).ToArray()) with { HasMore = true };
        var state = ConversationActivity.Apply(Snapshot(), first)!;
        var text = new ProcessItem("text", "text", "done", "answer", 81);
        state = ConversationActivity.Apply(state, Page(80, 90, text))!;
        state = ConversationActivity.Apply(state, Page(90, 100, text))!;
        Assert.Equal(81, state.ActiveRun!.OutputSnapshot.ProcessItems.Length);
        Assert.Equal("answer", state.ActiveRun.OutputSnapshot.Markdown);
        Assert.Equal(100, state.EventCursor);
        Assert.False(state.ActiveRun.OutputSnapshot.Window!.HasMoreBefore);
    }

    [Fact]
    public void LifecycleAndStaleOrForeignPagesNeverChangeCurrentState()
    {
        var current = Snapshot(10); var page = Page(10, 11);
        Assert.Null(ConversationActivity.Apply(current, page with { RequiresSnapshot = true }));
        Assert.Null(ConversationActivity.Apply(current, page with { Read = page.Read with { MainSessionId = "other" } }));
        Assert.Null(ConversationActivity.Apply(current, page with { Read = page.Read with { RunId = "other" } }));
        Assert.Null(ConversationActivity.Apply(current, page with { Read = page.Read with { TurnId = "other" } }));
        Assert.Null(ConversationActivity.Apply(current, page with { Read = page.Read with { AfterSequence = 9 } }));
        Assert.Null(ConversationActivity.Apply(current, page with { ThroughSequence = 9 }));
        Assert.Null(ConversationActivity.Apply(current, page with { ThroughSequence = 10, HasMore = true }));
        Assert.Equal(10, current.EventCursor);
    }

    [Fact]
    public void IrrelevantEventsCanAdvanceCursorWithoutInventingActivity()
    {
        var current = ConversationActivity.Apply(Snapshot(), Page(0, 27))!;
        Assert.Equal(27, current.EventCursor);
        Assert.Empty(current.ActiveRun!.OutputSnapshot.ProcessItems);
    }
}
