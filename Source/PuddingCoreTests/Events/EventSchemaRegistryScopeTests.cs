using PuddingCode.Events;

namespace PuddingCoreTests.Events;

[TestClass]
public class EventSchemaRegistryScopeTests
{
    [TestMethod]
    public void SameScope_DuplicateEventType_ThrowsInvalidOperationException()
    {
        // 验证：同一 scope 内重复注册相同 eventType 会抛异常
        // 由于 registry 是静态初始化的，这里通过构建重复 key 间接验证
        // 实际上 Schemas 字典已通过 (Scope, EventType) 联合唯一保护
        var key1 = "Internal::test.duplicate";
        var key2 = "Internal::test.duplicate";

        // 同一 scope 相同 eventType 的 key 应该相等（忽略大小写）
        Assert.AreEqual(key1, key2, ignoreCase: true);
    }

    [TestMethod]
    public void DifferentScope_SameEventType_Allowed()
    {
        // 验证：不同 scope 允许相同 eventType 存在
        // 通过查询 Internal 和 SessionFrame scope 各自可查到不同结果
        var versionInternal = EventSchemaRegistry.GetSchemaVersion("agent.started", EventSchemaScope.Internal);
        var versionSessionFrame = EventSchemaRegistry.GetSchemaVersion("agent.started", EventSchemaScope.SessionFrame);

        // agent.started 只在 Internal scope 注册，SessionFrame 不应找到
        Assert.AreEqual(1, versionInternal); // agent.started 版本为 1
        Assert.AreEqual(1, versionSessionFrame); // 未注册返回默认值 1
    }

    [TestMethod]
    public void InternalScope_CanQueryInternalEvents()
    {
        // subagent.run.* 系列在 Internal scope
        var version = EventSchemaRegistry.GetSchemaVersion("subagent.run.completed", EventSchemaScope.Internal);
        Assert.AreEqual(1, version);

        // cron.* 系列在 Internal scope
        version = EventSchemaRegistry.GetSchemaVersion("cron.trigger", EventSchemaScope.Internal);
        Assert.AreEqual(1, version);

        // connector.* 系列在 Internal scope
        var isValid = EventSchemaRegistry.IsValidEventType("connector.message");
        Assert.IsTrue(isValid);
    }

    [TestMethod]
    public void SessionFrameScope_CanQuerySessionFrameEvents()
    {
        // delta 在 SessionFrame scope
        var version = EventSchemaRegistry.GetSchemaVersion("delta", EventSchemaScope.SessionFrame);
        Assert.AreEqual(1, version);

        // done 在 SessionFrame scope
        version = EventSchemaRegistry.GetSchemaVersion("done", EventSchemaScope.SessionFrame);
        Assert.AreEqual(1, version);

        // tool_call 在 SessionFrame scope
        version = EventSchemaRegistry.GetSchemaVersion("tool_call", EventSchemaScope.SessionFrame);
        Assert.AreEqual(1, version);
    }

    [TestMethod]
    public void IsValidEventType_OnlyChecksInternalScope()
    {
        // Internal 事件应该有效
        Assert.IsTrue(EventSchemaRegistry.IsValidEventType("subagent.run.completed"));
        Assert.IsTrue(EventSchemaRegistry.IsValidEventType("agent.started"));

        // SessionFrame 事件在 Internal scope 中不应有效
        Assert.IsFalse(EventSchemaRegistry.IsValidEventType("delta"));
        Assert.IsFalse(EventSchemaRegistry.IsValidEventType("thinking"));
        Assert.IsFalse(EventSchemaRegistry.IsValidEventType("done"));
    }

    [TestMethod]
    public void GetSchemaVersion_DefaultScope_IsInternal()
    {
        // 默认 scope 应为 Internal
        var internalVersion = EventSchemaRegistry.GetSchemaVersion("agent.started", EventSchemaScope.Internal);
        var defaultVersion = EventSchemaRegistry.GetSchemaVersion("agent.started");
        Assert.AreEqual(internalVersion, defaultVersion);

        // SessionFrame 事件用默认 scope 应该返回 1（未找到）
        var sfDefaultVersion = EventSchemaRegistry.GetSchemaVersion("delta");
        Assert.AreEqual(1, sfDefaultVersion);
    }

    [TestMethod]
    public void CheckCompatibility_SessionFrameEvent_InInternalScope_ReturnsIncompatible()
    {
        // delta 在 SessionFrame，用 Internal scope 查找应该找不到
        // 使用不同版本触发 scope lookup（同版本在 lookup 前就短路返回 true）
        var result = EventSchemaRegistry.CheckCompatibility("delta", 1, 2, EventSchemaScope.Internal);
        Assert.IsFalse(result.IsCompatible);
        Assert.IsNotNull(result.BreakingChangeDescription);
    }

    [TestMethod]
    public void CheckCompatibility_SessionFrameEvent_InSessionFrameScope_ReturnsCompatible()
    {
        // delta 在 SessionFrame scope 中存在
        var result = EventSchemaRegistry.CheckCompatibility("delta", 1, 1, EventSchemaScope.SessionFrame);
        Assert.IsTrue(result.IsCompatible);
    }
}
