using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingCode.Runtime;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// B6（§3.2/§5）：Runtime 执行身份 → 浏览器调用者身份的投影。
/// 判据：子代理**必须**带父任务（否则无法做子集校验）；且**绝不**回退到默认身份。
/// </summary>
public sealed class BrowserCallerIdentitySourceSubAgentTests
{
    private static RuntimeExecutionIdentity Identity(
        RuntimeExecutionKind kind, string runId, string? parentRunId = null) => new()
    {
        Kind = kind,
        ConversationId = "conv-1",
        RunId = runId,
        TraceId = null,
        ParentRunId = parentRunId,
    };

    [Fact]
    public void ASubAgentIsProjectedWithItsParentTask()
    {
        var ok = BrowserCallerIdentitySource.TryProject(
            Identity(RuntimeExecutionKind.SubAgent, "run-child", "run-parent"),
            agentInstanceId: "agent-1",
            out var caller,
            out var reason);

        Assert.True(ok, reason);
        Assert.NotNull(caller);
        Assert.Equal(BrowserCallerKind.SubAgent, caller!.Kind);
        Assert.Equal("run-child", caller.TaskId);
        Assert.Equal("run-parent", caller.ParentTaskId);
        Assert.Equal("agent-1", caller.CallerId);
    }

    [Fact]
    public void ASubAgentWithoutAParentIsRefusedWithAConcreteReason()
    {
        var ok = BrowserCallerIdentitySource.TryProject(
            Identity(RuntimeExecutionKind.SubAgent, "run-child"),
            agentInstanceId: "agent-1",
            out var caller,
            out var reason);

        Assert.False(ok);
        Assert.Null(caller); // 绝不回退到默认身份
        Assert.Contains("parent", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AConversationTurnIsProjectedAsAnAgentWithoutAParent()
    {
        var ok = BrowserCallerIdentitySource.TryProject(
            Identity(RuntimeExecutionKind.ConversationTurn, "run-parent"),
            agentInstanceId: "agent-1",
            out var caller,
            out var reason);

        Assert.True(ok, reason);
        Assert.Equal(BrowserCallerKind.Agent, caller!.Kind);
        Assert.Equal("run-parent", caller.TaskId);
        Assert.Null(caller.ParentTaskId);
    }

    [Fact]
    public void MissingExecutionIdentityIsRefusedRatherThanInferred()
    {
        var ok = BrowserCallerIdentitySource.TryProject(null, "agent-1", out var caller, out var reason);

        Assert.False(ok);
        Assert.Null(caller);
        Assert.Contains("no trusted execution identity", reason, StringComparison.Ordinal);
    }
}