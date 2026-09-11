using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-B-1 测试方案 A（C01B-AC2/AC8）：把 composition 的「先后顺序」（revision，严格单调不复用）
/// 与「内容身份」（ContentId，内容相同可复用）拆成两个独立维度。
/// A→B→A ⇒ Revision = 1,2,3；ContentId = A,B,A。
/// </summary>
[TestClass]
public sealed class CompositionRevisionContentIdTests
{
    private static SessionCompositionRecord Record(long version, string sysHash, string toolHash) => new()
    {
        SessionId = "s1",
        CompositionVersion = version,
        SystemPromptHash = sysHash,
        ToolSpecHash = toolHash,
        PrefixHash = CompositionSnapshot.ComputePrefixHash(sysHash, toolHash),
        ContentId = CompositionSnapshot.ComputeContentId(sysHash, toolHash),
        ToolIds = Array.Empty<string>(),
    };

    [TestMethod]
    public void Observe_A_B_A_RevisionIsMonotonic_ContentIdReused()
    {
        var reg = new CompositionVersionRegistry();

        var a1 = reg.Observe("s1", "sys-A", "tool-A");
        var b = reg.Observe("s1", "sys-B", "tool-A");
        var a2 = reg.Observe("s1", "sys-A", "tool-A");

        Assert.AreEqual(1L, a1.Revision);
        Assert.AreEqual(2L, b.Revision);
        Assert.AreEqual(3L, a2.Revision, "A→B→A 的 revision 必须继续递增（不复用旧 revision）。");

        Assert.AreEqual(a1.ContentId, a2.ContentId, "内容回到 A ⇒ ContentId 必须复用。");
        Assert.AreNotEqual(a1.ContentId, b.ContentId, "不同内容的 ContentId 必须不同。");
        Assert.AreEqual(CompositionSnapshot.ComputeContentId("sys-A", "tool-A"), a2.ContentId);
    }

    [TestMethod]
    public void Observe_SameContentTwice_ReusesContentId_ButAdvancesRevision()
    {
        var reg = new CompositionVersionRegistry();

        var first = reg.Observe("s1", "sys-A", "tool-A");
        var second = reg.Observe("s1", "sys-A", "tool-A");

        Assert.AreEqual(1L, first.Revision);
        Assert.AreEqual(2L, second.Revision, "同一观测点仍推进 revision（先后顺序维度）。");
        Assert.AreEqual(first.ContentId, second.ContentId, "相同内容复用同一 ContentId。");
        Assert.AreEqual("none", second.ChangeReason, "内容未变 ⇒ changeReason 仍为 none。");
    }

    [TestMethod]
    public void Observe_LongRevision_NotSaturated()
    {
        var reg = new CompositionVersionRegistry();
        var floor = (long)int.MaxValue + 5;

        reg.Seed("s1", new[] { Record(floor, "sys-a", "tool-a") });
        var observation = reg.Observe("s1", "sys-b", "tool-a");

        Assert.AreEqual(floor + 1, observation.Revision, "revision 必须从已持久化 max+1 继续，long 不塌缩。");
        Assert.IsTrue(observation.Revision > int.MaxValue, "revision 不得被饱和折叠回 int 范围（R3 已删除 ToInternalVersion）。");
    }

    [TestMethod]
    public void Seed_ThenObserve_A_B_A_ContinuesFromPersistedMax()
    {
        var reg = new CompositionVersionRegistry();
        reg.Seed("s1", new[]
        {
            Record(1, "sys-A", "tool-A"),
            Record(2, "sys-B", "tool-A"),
            Record(3, "sys-A", "tool-A"),
        });

        var observation = reg.Observe("s1", "sys-B", "tool-A");

        Assert.AreEqual(4L, observation.Revision, "重启恢复后不得复用已持久化 revision，必须继续递增。");
        Assert.AreEqual(
            CompositionSnapshot.ComputeContentId("sys-B", "tool-A"),
            observation.ContentId,
            "恢复后 ContentId 仍按内容派生（可复用，与 revision 无关）。");
    }
}
