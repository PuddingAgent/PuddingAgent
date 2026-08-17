using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentSessionManagerTests
{
    [TestMethod]
    public void GetOrCreate_UsesPreferredAgentInstanceId_ForNewSession()
    {
        var manager = new AgentSessionManager();

        var instance = manager.GetOrCreate(
            "session-1",
            "global:general-assistant",
            preferredAgentInstanceId: "default.global_general-assistant.1c3");

        Assert.AreEqual("default.global_general-assistant.1c3", instance.AgentInstanceId);
    }

    [TestMethod]
    public void GetOrCreate_DoesNotReplaceExistingAgentInstanceId()
    {
        var manager = new AgentSessionManager();

        var first = manager.GetOrCreate(
            "session-1",
            "global:general-assistant",
            preferredAgentInstanceId: "agent-a");
        var second = manager.GetOrCreate(
            "session-1",
            "global:general-assistant",
            preferredAgentInstanceId: "agent-b");

        Assert.AreEqual("agent-a", first.AgentInstanceId);
        Assert.AreEqual("agent-a", second.AgentInstanceId);
    }

    [TestMethod]
    public void LoadedToolIds_Persist_ForSession_And_AreRemoved_WithSession()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate("session-1", "global:general-assistant");

        manager.RememberLoadedToolIds("session-1", ["file_read", "search_grep"]);

        var loaded = manager.GetLoadedToolIds("session-1");
        CollectionAssert.AreEquivalent(new[] { "file_read", "search_grep" }, loaded.ToArray());

        manager.Remove("session-1");
        Assert.IsEmpty(manager.GetLoadedToolIds("session-1"));
    }
}
