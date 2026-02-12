using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentExecutionStateRegistryTests
{
    [TestMethod]
    public void Get_ReturnsIdleWhenAgentHasNoExecution()
    {
        var registry = new AgentExecutionStateRegistry();

        var state = registry.Get("workspace-a", "agent-a");

        Assert.AreEqual("workspace-a", state.WorkspaceId);
        Assert.AreEqual("agent-a", state.AgentId);
        Assert.AreEqual("idle", state.Status);
        Assert.IsTrue(state.CanStartMessageDelivery);
        Assert.IsNull(state.CurrentExecutionId);
    }

    [TestMethod]
    public void TryBegin_MarksAgentBusyUntilMatchingExecutionCompletes()
    {
        var registry = new AgentExecutionStateRegistry();

        Assert.IsTrue(registry.TryBegin("workspace-a", "agent-a", "exec-1", "first task"));
        Assert.IsFalse(registry.TryBegin("workspace-a", "agent-a", "exec-2", "second task"));

        var busy = registry.Get("workspace-a", "agent-a");
        Assert.AreEqual("busy", busy.Status);
        Assert.AreEqual("exec-1", busy.CurrentExecutionId);
        Assert.AreEqual("first task", busy.CurrentTask);
        Assert.IsFalse(busy.CanStartMessageDelivery);

        Assert.IsFalse(registry.Complete("workspace-a", "agent-a", "exec-2"));
        Assert.AreEqual("busy", registry.Get("workspace-a", "agent-a").Status);

        Assert.IsTrue(registry.Complete("workspace-a", "agent-a", "exec-1"));
        Assert.AreEqual("idle", registry.Get("workspace-a", "agent-a").Status);
    }

    [TestMethod]
    public async Task DefaultAvailabilityProvider_ReadsRegistryState()
    {
        var registry = new AgentExecutionStateRegistry();
        var provider = new DefaultAgentExecutionAvailabilityProvider(registry);

        registry.TryBegin("workspace-a", "agent-a", "exec-1", "first task");

        var state = await provider.GetAsync("workspace-a", "agent-a");

        Assert.AreEqual("busy", state.Status);
        Assert.AreEqual("exec-1", state.CurrentExecutionId);
        Assert.IsFalse(state.CanStartMessageDelivery);
    }
}
