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
    public void LoadedToolIds_Persist_ForSession_And_Survive_Remove()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate("session-1", "global:general-assistant");

        manager.RememberLoadedToolIds("session-1", ["file_read", "search_grep"]);

        var loaded = manager.GetLoadedToolIds("session-1");
        CollectionAssert.AreEquivalent(new[] { "file_read", "search_grep" }, loaded.ToArray());

        // 工具集合 append-only：清理实例不清理已授权工具面。
        manager.Remove("session-1");
        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep" },
            manager.GetLoadedToolIds("session-1").ToArray());
    }

    [TestMethod]
    public void CleanupExpired_DoesNotShrink_LoadedToolSet()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate(
            "session-1",
            "global:general-assistant",
            sessionTimeout: TimeSpan.FromMilliseconds(1));
        manager.RememberLoadedToolIds("session-1", ["file_read", "search_grep"]);

        Thread.Sleep(5); // 确保超过 1ms 超时窗口

        var removed = manager.CleanupExpired();
        CollectionAssert.Contains(removed.ToArray(), "session-1");
        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep" },
            manager.GetLoadedToolIds("session-1").ToArray());
    }

    [TestMethod]
    public void SnapshotToolSet_ReturnsFullImmutableSnapshot()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate("session-1", "global:general-assistant");

        // 无记录 → 空集
        Assert.IsEmpty(manager.SnapshotToolSet("session-1"));

        manager.RememberLoadedToolIds("session-1", ["file_read", "search_grep"]);
        var snapshot = manager.SnapshotToolSet("session-1");
        CollectionAssert.AreEquivalent(new[] { "file_read", "search_grep" }, snapshot.ToArray());

        // 快照是副本：外部修改不影响内部状态
        if (snapshot is HashSet<string> mutable)
            mutable.Add("external_mutation");
        CollectionAssert.DoesNotContain(
            manager.SnapshotToolSet("session-1").ToArray(),
            "external_mutation");
    }

    [TestMethod]
    public void HydrateToolIds_AppendOnly_DoesNotShrinkExisting()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate("session-1", "global:general-assistant");
        manager.RememberLoadedToolIds("session-1", ["file_read"]);

        // 从持久化水合：已存在的 file_read 不重复，新增 search_grep 追加，不收缩。
        manager.HydrateToolIds("session-1", ["file_read", "search_grep"]);

        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_grep" },
            manager.GetLoadedToolIds("session-1").ToArray());
    }

    [TestMethod]
    public void ToolUsageRecency_IsMonotonic_And_PerSession()
    {
        var manager = new AgentSessionManager();

        Assert.IsEmpty(manager.GetToolUsageRecency("session-1"));

        manager.TouchToolUsage("session-1", "file_read");
        manager.TouchToolUsage("session-1", "search_grep");
        manager.TouchToolUsage("session-2", "shell");

        var session1 = manager.GetToolUsageRecency("session-1");
        Assert.AreEqual(2, session1.Count);
        Assert.IsGreaterThan(0, session1["file_read"]);
        Assert.IsGreaterThan(session1["file_read"], session1["search_grep"], "后使用的工具序号更大");

        manager.TouchToolUsage("session-1", "file_read");
        var afterRetouch = manager.GetToolUsageRecency("session-1");
        Assert.IsGreaterThan(afterRetouch["search_grep"], afterRetouch["file_read"]);

        Assert.IsFalse(manager.GetToolUsageRecency("session-2").ContainsKey("file_read"), "使用序不跨会话泄漏");
    }
}
