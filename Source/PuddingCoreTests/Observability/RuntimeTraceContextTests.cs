using PuddingCode.Observability;

namespace PuddingCoreTests.Observability;

[TestClass]
public sealed class RuntimeTraceContextTests
{
    [TestMethod]
    public void CreateNew_generates_trace_and_correlation_ids()
    {
        var ctx = RuntimeTraceContext.CreateNew(
            sessionId: "s1",
            workspaceId: "w1",
            userId: "u1");

        Assert.IsFalse(string.IsNullOrWhiteSpace(ctx.TraceId));
        Assert.AreEqual(ctx.TraceId, ctx.CorrelationId);
        Assert.AreEqual("s1", ctx.SessionId);
        Assert.AreEqual("w1", ctx.WorkspaceId);
        Assert.AreEqual("u1", ctx.UserId);
    }

    [TestMethod]
    public void CreateChildExecution_preserves_trace_and_sets_parent()
    {
        var parent = RuntimeTraceContext.CreateNew(
            sessionId: "parent-session",
            workspaceId: "w1",
            executionId: "exec-parent");

        var child = parent.CreateChildExecution(
            sessionId: "child-session",
            executionId: "exec-child",
            subAgentId: "sub-1");

        Assert.AreEqual(parent.TraceId, child.TraceId);
        Assert.AreEqual(parent.CorrelationId, child.CorrelationId);
        Assert.AreEqual("exec-parent", child.ParentExecutionId);
        Assert.AreEqual("exec-child", child.ExecutionId);
        Assert.AreEqual("sub-1", child.SubAgentId);
    }
}
