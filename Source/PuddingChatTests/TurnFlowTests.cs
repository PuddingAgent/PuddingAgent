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

    [Fact]
    public void ToolTreeUsesExplicitParentsAndSurvivesCyclesAndMissingParents()
    {
        var flow = PuddingChat.TurnFlow.Build([
            new("a", "tool_call", "running", "", 1, ToolCallId: "root"),
            new("b", "tool_call", "running", "", 2, ToolCallId: "other"),
            new("c", "tool_call", "running", "", 3, ToolCallId: "child", ParentToolCallId: "root"),
            new("d", "tool_call", "running", "", 4, ToolCallId: "orphan", ParentToolCallId: "missing"),
            new("e", "tool_call", "running", "", 5, ToolCallId: "x", ParentToolCallId: "y"),
            new("f", "tool_call", "running", "", 6, ToolCallId: "y", ParentToolCallId: "x")], "");
        Assert.Equal(6, flow.Length);
        Assert.Equal("tool::child", flow[1].Key); Assert.Equal(1, flow[1].Depth);
        Assert.All(flow.Where(b => b.Key != "tool::child"), b => Assert.Equal(0, b.Depth));
    }

    [Fact]
    public void EarlierResultDoesNotGetOverwrittenByLaterCall()
    {
        var flow = PuddingChat.TurnFlow.Build([
            new("r", "tool_result", "error", "", 1, Output: "failed", ExitCode: 2, ToolCallId: "a"),
            new("c", "tool_call", "running", "", 2, "terminal", Arguments: "build", ToolCallId: "a")], "");
        var tool = Assert.Single(flow);
        Assert.Equal("error", tool.Status); Assert.Equal("failed", tool.Output);
        Assert.Equal("build", tool.Arguments); Assert.Equal(2, tool.ExitCode);
    }

    [Fact]
    public void ReusedSubagentGetsIndependentExecutionCards()
    {
        var flow = PuddingChat.TurnFlow.Build([
            new("a", "delegation", "running", "task A", 1, "builder", DelegationRunId: "pooled", DelegationExecutionId: "run-a"),
            new("b", "delegation", "success", "reply A", 2, DelegationRunId: "pooled", DelegationExecutionId: "run-a"),
            new("c", "delegation", "running", "task B", 3, "builder", DelegationRunId: "pooled", DelegationExecutionId: "run-b"),
            new("d", "delegation", "timed_out", "timeout B", 4, DelegationRunId: "pooled", DelegationExecutionId: "run-b")], "");
        Assert.Equal(2, flow.Length); Assert.Equal("task A", flow[0].Text);
        Assert.Equal("reply A", flow[0].Output); Assert.Equal("success", flow[0].Status);
        Assert.Equal("task B", flow[1].Text); Assert.Equal("超时", PuddingChat.TurnFlow.StatusLabel(flow[1].Status));
    }

    [Fact]
    public void MissingExecutionIdentityDoesNotMergeBySubagentName()
    {
        var flow = PuddingChat.TurnFlow.Build([
            new("a", "delegation", "running", "A", 1, "same", DelegationRunId: "pooled"),
            new("b", "delegation", "success", "B", 2, "same", DelegationRunId: "pooled")], "");
        Assert.Equal(2, flow.Length);
    }

    [Fact]
    public void NonzeroExitIsFailureAndParentCannotCrossTurns()
    {
        var flow = PuddingChat.TurnFlow.Build([
            new("a", "tool_call", "running", "", 1, ToolCallId: "parent", TurnId: "one"),
            new("b", "tool_call", "running", "", 2, ToolCallId: "child", TurnId: "two", ParentToolCallId: "parent"),
            new("c", "tool_result", "success", "", 3, ExitCode: 1, ToolCallId: "child", TurnId: "two")], "");
        Assert.Equal(0, flow[1].Depth); Assert.Equal("error", flow[1].Status);
    }
}
