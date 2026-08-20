using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P0-5 缺陷修复：<see cref="CompositionVersionRegistry.Seed"/> 预热已持久化版本，
/// 保证 Core 重启后同组合复用已存版本、新组合从 max+1 继续、首 Observe 基线不误报。
/// </summary>
[TestClass]
public sealed class CompositionVersionRegistrySeedTests
{
    private static SessionCompositionRecord Record(
        long version,
        string sysHash,
        string toolHash,
        int permissionEpoch = 0) => new()
    {
        SessionId = "s1",
        CompositionVersion = version,
        SystemPromptHash = sysHash,
        ToolSpecHash = toolHash,
        PrefixHash = CompositionSnapshot.ComputePrefixHash(sysHash, toolHash),
        ToolIds = Array.Empty<string>(),
        PermissionEpoch = permissionEpoch,
    };

    [TestMethod]
    public void Seed_SameCombo_ReusesPersistedVersion()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a"),
            Record(2, "sys-b", "tool-a"),
        });

        // 与已持久化记录完全相同的组合 → 复用已存版本 1，而不是重新分配。
        var observation = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(1, observation.Version);
        Assert.AreEqual("system_prompt_changed", observation.ChangeReason); // 相对最新基线 sys-b 确实变化

        // 连续观察稳定复用。
        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Version);
    }

    [TestMethod]
    public void Seed_NewCombo_ContinuesFromMaxPlusOne()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a"),
            Record(10, "sys-b", "tool-a"),
        });

        var observation = registry.Observe("s1", "sys-new", "tool-a");

        Assert.AreEqual(11, observation.Version); // max=10 → 从 11 开始
    }

    [TestMethod]
    public void Seed_FirstObserve_SameAsLatestBaseline_DoesNotReportSystemPromptChanged()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-old", "tool-old", permissionEpoch: 1),
            Record(10, "sys-latest", "tool-latest", permissionEpoch: 3),
        });

        // 首 Observe 与最新基线（sys-latest/tool-latest）相同 → 不误报 system_prompt_changed。
        var observation = registry.Observe("s1", "sys-latest", "tool-latest");

        Assert.AreEqual(10, observation.Version);
        Assert.AreEqual("none", observation.ChangeReason);
    }

    [TestMethod]
    public void Seed_FirstObserve_DifferentCombo_ReportsChange()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a"),
            Record(2, "sys-b", "tool-a"),
        });

        var observation = registry.Observe("s1", "sys-c", "tool-a");

        Assert.AreEqual(3, observation.Version);
        Assert.AreEqual("system_prompt_changed", observation.ChangeReason);
    }

    [TestMethod]
    public void Seed_PermissionEpochBaseline_IsRetained()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a", permissionEpoch: 0),
            Record(5, "sys-b", "tool-b", permissionEpoch: 7),
        });

        // 未传指纹、显式 epoch=0 → 内部基线 7 保留（下限语义，不回落）。
        var observation = registry.Observe("s1", "sys-b", "tool-b", permissionEpoch: 0);

        Assert.AreEqual(7, observation.PermissionEpoch);
    }

    [TestMethod]
    public void Seed_EmptyRecords_IsNoOp()
    {
        var registry = new CompositionVersionRegistry();

        registry.Seed("s1", Array.Empty<SessionCompositionRecord>());

        var observation = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(1, observation.Version);
        Assert.AreEqual("initial", observation.ChangeReason);
    }

    [TestMethod]
    public void Seed_IsScopedPerSession()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[] { Record(1, "sys-a", "tool-a") });

        // 未 seed 的 session 从 1 开始，不受 s1 影响。
        Assert.AreEqual(1, registry.Observe("s2", "sys-a", "tool-a").Version);
        // 已 seed 的 session 复用 1。
        Assert.AreEqual(1, registry.Observe("s1", "sys-a", "tool-a").Version);
    }
}
