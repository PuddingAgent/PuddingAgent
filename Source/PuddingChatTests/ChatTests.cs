using PuddingChat;

namespace PuddingChatTests;

public class ChatTests
{
    private static readonly RoleKey A = new("workspace", "builder");
    private static Conversation View(RoleKey role, long cursor, string session = "session") =>
        new(role.WorkspaceId, role.AgentId, session, [], null, cursor);

    [Fact] public void SelectionRejectsLateResponsesAndKeepsDraftsPerWorkspace()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "first";
        var epoch = state.Generation;
        var other = new RoleKey("other", A.AgentId); state.Select(other); state.Draft = "second";
        Assert.False(state.Apply(epoch, View(A, 10)));
        Assert.False(state.Apply(state.Generation, View(A, 10)));
        Assert.Equal("second", state.Draft);
        state.Select(A); Assert.Equal("first", state.Draft);
    }
    [Fact] public void RetryRetainsCommandIdentityAndDoesNotClearNewDraft()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "implement";
        var pending = state.Prepare("session"); state.Draft = "new draft";
        Assert.Same(pending, state.Prepare("session"));
        state.Select(new("other", "reviewer")); state.Draft = "review";
        state.Accept(pending); Assert.Equal("review", state.Draft);
        state.Select(A); Assert.Equal("new draft", state.Draft); Assert.Null(state.Pending);
        Assert.NotEqual(pending.ClientRequestId, state.Prepare("session").ClientRequestId);
    }
    [Fact] public void MatchingReceiptClearsOnlyOriginalDraft()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "hello";
        state.Accept(state.Prepare("s")); Assert.Equal("", state.Draft);
    }
    [Fact] public void CursorOrderingAllowsSessionRotation()
    {
        var state = new ChatSelection(); state.Select(A);
        Assert.True(state.Apply(state.Generation, View(A, 20)));
        Assert.False(state.Apply(state.Generation, View(A, 19)));
        Assert.True(state.Apply(state.Generation, View(A, 1, "rotated")));
    }
    [Fact] public void EventsUseCanonicalSequenceAndNeverPairToolsByName()
    {
        var first = new ProcessItem("a", "tool_call", "running", "", 5, "shell", ToolCallId: "call1");
        var second = new ProcessItem("b", "tool_call", "running", "", 3, "shell", ToolCallId: "call2");
        Assert.Equal(new[] { second, first }, ChatSelection.Ordered([first, second, first]));
        Assert.Null(ChatSelection.ActiveTurn(View(A, 0)));
    }
    [Fact] public void BoundaryContainsOnlyBclDependencies()
    {
        var refs = typeof(ChatSelection).Assembly.GetReferencedAssemblies();
        Assert.All(refs, r => Assert.True(r.Name!.StartsWith("System.") || r.Name is "System" or "netstandard", r.Name));
    }
    [Fact] public void AccountResetDropsDraftsAndPendingCommands()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "private"; state.Prepare("s");
        state.Clear(); state.Select(A); Assert.Equal("", state.Draft); Assert.Null(state.Pending);
    }
    [Fact] public void RejectedValidationKeepsDraftButAllowsCorrection()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "old"; var pending = state.Prepare("s");
        state.Reject(pending); Assert.Equal("old", state.Draft); state.Draft = "corrected";
        var corrected = state.Prepare("s"); Assert.Equal("corrected", corrected.Text); Assert.NotEqual(pending.ClientRequestId, corrected.ClientRequestId);
    }
    [Fact] public void SessionCreationDoesNotCaptureTextTypedAfterSend()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "first";
        var captured = state.Draft; state.Draft = "next";
        var send = state.Prepare("created-session", captured); Assert.Equal("first", send.Text);
        state.Accept(send); Assert.Equal("next", state.Draft);
    }
}
