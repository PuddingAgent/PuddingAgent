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
    public void Seed_SameCombo_ReusesContentId_SameComboRevisionContinuesFromMax()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a"),
            Record(2, "sys-b", "tool-a"),
        });

        // C01-B：revision 不复用——已持久化 max=2 ⇒ 继续递增到 3；内容身份复用同一 ContentId。
        var observation = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(3, observation.Revision);
        Assert.AreEqual("system_prompt_changed", observation.ChangeReason); // 相对最新基线 sys-b 确实变化
        Assert.AreEqual(CompositionSnapshot.ComputeContentId("sys-a", "tool-a"), observation.ContentId);

        // 连续观察：revision 继续递增，ContentId 仍相同。
        var again = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(4, again.Revision);
        Assert.AreEqual(observation.ContentId, again.ContentId);
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

        Assert.AreEqual(11, observation.Revision); // max=10 → 从 11 开始
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

        Assert.AreEqual(11, observation.Revision);
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

        Assert.AreEqual(3, observation.Revision);
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
        Assert.AreEqual(1, observation.Revision);
        Assert.AreEqual("initial", observation.ChangeReason);
    }

    [TestMethod]
    public void Seed_IsScopedPerSession()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[] { Record(1, "sys-a", "tool-a") });

        // 未 seed 的 session 从 1 开始，不受 s1 影响。
        Assert.AreEqual(1, registry.Observe("s2", "sys-a", "tool-a").Revision);
        // 已 seed 的 session 从 max+1 继续（revision 不复用）。
        Assert.AreEqual(2, registry.Observe("s1", "sys-a", "tool-a").Revision);
    }

    // ── P0-5 指纹基线（重启恢复）回归 ───────────────────────

    /// <summary>
    /// 🔴 回归（本次缺陷）：Seed 设置 _hasLast=true 但 _lastPermissionFingerprint 保持 null，
    /// 旧实现首轮非空指纹 Observe 必然误报 permission_changed（epoch 虚增 +1、
    /// 同 hash 组合被强制开新版本、changeReason 污染）。修复后由 _hasFingerprintBaseline
    /// 独立标志控制：Seed 不置位，首轮非空指纹仅建立基线，不触发变化。
    /// </summary>
    [TestMethod]
    public void Seed_FirstObserve_WithNonEmptyFingerprint_SameCombo_DoesNotReportPermissionChanged()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a", permissionEpoch: 1),
            Record(10, "sys-latest", "tool-latest", permissionEpoch: 3),
        });

        // 重启后首轮：非空指纹 + 与 Seed 基线（最新记录）相同 system/tool hash。
        var observation = registry.Observe(
            "s1", "sys-latest", "tool-latest", permissionEpoch: 0, permissionFingerprint: "fp-1");

        // 同 hash 组合在 C01-B 下 revision 仍然递增（不复用），且不得被强制开新版本的旧逻辑污染。
        Assert.AreEqual(11, observation.Revision, "重启后 revision 必须从已持久化 max(10)+1 继续。");
        // changeReason 不得被污染。
        Assert.IsFalse(
            observation.ChangeReason.Contains("permission_changed", StringComparison.Ordinal),
            "重启后首轮不得误报 permission_changed（实际：" + observation.ChangeReason + "）。");
        // epoch 不得虚增（显式 0 仅为下限，Seed 基线 3 保留）。
        Assert.AreEqual(3, observation.PermissionEpoch, "epoch 不得虚增。");
    }

    /// <summary>
    /// 标志链路生效验证：首轮非空指纹建立基线后，第二轮不同指纹必须正常触发
    /// permission_changed（开新版本 + epoch +1 + changeReason 上报），升级语义保留。
    /// </summary>
    [TestMethod]
    public void Seed_SecondObserve_WithDifferentFingerprint_ReportsPermissionChanged()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[] { Record(1, "sys-a", "tool-a", permissionEpoch: 0) });

        var first = registry.Observe("s1", "sys-a", "tool-a", permissionFingerprint: "fp-1");
        Assert.AreEqual(2, first.Revision);
        Assert.IsFalse(first.ChangeReason.Contains("permission_changed", StringComparison.Ordinal));
        Assert.AreEqual(0, first.PermissionEpoch);

        // 第二指纹与基线不同 → 触发 permission_changed：新 revision + epoch +1 + reason 上报。
        var second = registry.Observe("s1", "sys-a", "tool-a", permissionFingerprint: "fp-2");
        Assert.AreEqual(3, second.Revision, "权限变化必须开新 revision（revision 不复用）。");
        Assert.IsTrue(second.ChangeReason.Contains("permission_changed", StringComparison.Ordinal));
        Assert.AreEqual(1, second.PermissionEpoch, "权限纪元应自增 +1。");
    }

    // ── Seed 单测全集补强（防分叉/幂等） ────────────────────

    /// <summary>多条记录含同组合不同版本：Seed 必须保留最大版本（防版本分叉）。</summary>
    [TestMethod]
    public void Seed_DuplicateCombo_KeepsLargestRevisionFloor_NoFork()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[]
        {
            Record(1, "sys-a", "tool-a"),
            Record(4, "sys-a", "tool-a"), // 同组合更高版本：大版本覆盖
        });

        var observation = registry.Observe("s1", "sys-a", "tool-a");
        Assert.AreEqual(5, observation.Revision, "同组合多条记录必须把 revision 下界抬到 max(4)+1，防分叉/倒退。");
        Assert.AreEqual("none", observation.ChangeReason); // 与最新基线一致
    }

    /// <summary>重复 Seed：版本不得倒退（幂等），新组合仍从 max+1 继续。</summary>
    [TestMethod]
    public void Seed_Repeated_IsIdempotent_VersionsDoNotRegress()
    {
        var registry = new CompositionVersionRegistry();
        registry.Seed("s1", new[] { Record(1, "sys-a", "tool-a"), Record(2, "sys-b", "tool-a") });
        registry.Seed("s1", new[] { Record(2, "sys-b", "tool-a"), Record(3, "sys-c", "tool-a") });

        // 新组合从 max+1=4 继续（不因重复 Seed 倒退）。
        Assert.AreEqual(4, registry.Observe("s1", "sys-new", "tool-a").Revision);
        // 旧组合的 revision 也继续递增（不复用），但不得倒退到已持久化 revision。
        Assert.AreEqual(5, registry.Observe("s1", "sys-a", "tool-a").Revision);
        Assert.AreEqual(6, registry.Observe("s1", "sys-b", "tool-a").Revision);
    }
}
