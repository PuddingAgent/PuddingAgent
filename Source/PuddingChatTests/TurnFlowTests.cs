namespace PuddingChatTests;

public sealed class TurnFlowTests
{
    [Fact]
    public void InterleavesSegmentsAndPairsParallelToolsById()
    {
        var items = new PuddingChat.ProcessItem[] {
            new("1", "thinking", "running", "思", 1), new("2", "thinking", "running", "考", 2),
            new("3", "text", "done", "先检查", 3),
            new("4", "tool_call", "running", "", 4, "terminal", Arguments: "A", ToolCallId: "a"),
            new("5", "tool_call", "running", "", 5, "terminal", Arguments: "B", ToolCallId: "b"),
            new("6", "tool_result", "error", "", 6, Output: "B failed", ExitCode: 1, ToolCallId: "b"),
            new("7", "thinking", "running", "再检查", 7), new("8", "text", "done", "结束", 8) };
        var flow = PuddingChat.TurnFlow.Build(items.Reverse().Concat([items[0]]), "不得重复正文");
        Assert.Equal(new[] { "thinking", "text", "tool", "tool", "thinking", "text" }, flow.Select(b => b.Kind));
        Assert.Equal("思考", flow[0].Text);
        Assert.Equal("running", flow[2].Status);
        Assert.Equal("B", flow[3].Arguments); Assert.Equal("B failed", flow[3].Output); Assert.Equal(1, flow[3].ExitCode);
    }

    [Fact]
    public void MissingToolIdentityDoesNotMergeAndFallbackAppearsOnce()
    {
        var flow = PuddingChat.TurnFlow.Build([
            new("1", "tool_call", "running", "", 1, "same"),
            new("2", "tool_result", "success", "result", 2, "same")], "answer");
        Assert.Equal(3, flow.Length);
        Assert.Equal("answer", flow[^1].Text);
        Assert.Empty(PuddingChat.TurnFlow.Build([], ""));
    }
}
