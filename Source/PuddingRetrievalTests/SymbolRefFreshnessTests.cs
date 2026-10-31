using PuddingRetrieval;
using PuddingRetrieval.Contracts;

namespace PuddingRetrievalTests;

/// <summary>
/// <see cref="SymbolRefFreshness"/> 的三态判定契约（symbol_ref 提案 §5/§6）。
/// <para>
/// 守的是三条：①只有 content_hash 相等才算 Fresh（§8.3：不得沿旧行号打开当前代码）；
/// ②重定位成功**仍是 Stale**（"找到候选" ≠ "旧 ref 仍有效"）；
/// ③位置相关/未知形态的 id 一律不可重定位，零候选的措辞不得暗示符号不存在。
/// </para>
/// </summary>
[TestClass]
public sealed class SymbolRefFreshnessTests
{
    private const string CurrentHash = "aaaa1111";
    private const string OlderHash = "bbbb2222";

    private static SymbolRefV1 Reference(string symbolId = "M:Ns.Type.Run") =>
        SymbolRefV1.Create("proj-1", "Source/A.cs", symbolId, 10, 20, OlderHash, "roslyn-v1");

    private static SymbolRefRelocation Candidate(int startLine) =>
        new("source/a.cs", startLine, startLine + 5);

    /// <summary>hash 一致 ⇒ 行号权威，且不允许携带任何候选（Fresh 与"待重定位"互斥）。</summary>
    [TestMethod]
    public void EqualHash_IsFresh_AndCarriesNoRelocation()
    {
        // 必须传 ref 记录的那个 hash（OlderHash）—— 传 CurrentHash 就变成"hash 不同"，
        // 断言会期望 Fresh 而得到 Stale（本用例曾被 M1 变异掩盖成"通过"，见提交说明）。
        var result = SymbolRefFreshness.Evaluate(Reference(), OlderHash, new[] { Candidate(99) });

        Assert.AreEqual(SymbolRefState.Fresh, result.State);
        Assert.IsFalse(result.CanRelocate);
        Assert.IsEmpty(result.Relocations);
        StringAssert.Contains(result.Reason, "权威");
    }

    /// <summary>hash 不同 + 声明式 id + 唯一候选 ⇒ Stale 但可重定位；**不得**因此变成 Fresh。</summary>
    [TestMethod]
    public void ChangedHash_UniqueCandidate_IsStaleButRelocatable()
    {
        var result = SymbolRefFreshness.Evaluate(Reference(), CurrentHash, new[] { Candidate(120) });

        Assert.AreEqual(SymbolRefState.Stale, result.State);
        Assert.IsTrue(result.CanRelocate);
        Assert.AreEqual(1, result.Relocations.Count);
        Assert.AreEqual(120, result.Relocations[0].StartLine);
        StringAssert.Contains(result.Reason, "不表示旧行号仍然有效");
    }

    /// <summary>零候选：必须说"无法定位"，且不得断言符号不存在（§8.3 范围缺口不能否定存在）。</summary>
    [TestMethod]
    public void ChangedHash_NoCandidate_SaysCannotLocate_NotSymbolMissing()
    {
        var result = SymbolRefFreshness.Evaluate(Reference(), CurrentHash, null);

        Assert.AreEqual(SymbolRefState.Stale, result.State);
        Assert.IsFalse(result.CanRelocate);
        Assert.IsEmpty(result.Relocations);
        StringAssert.Contains(result.Reason, "无法定位");
        Assert.DoesNotContain("不存在", result.Reason);
    }

    /// <summary>多候选 ⇒ 不可重定位，但候选必须原样返回供调用方消歧。</summary>
    [TestMethod]
    public void ChangedHash_MultipleCandidates_IsAmbiguous()
    {
        var result = SymbolRefFreshness.Evaluate(Reference(), CurrentHash, new[] { Candidate(30), Candidate(60) });

        Assert.AreEqual(SymbolRefState.Stale, result.State);
        Assert.IsFalse(result.CanRelocate);
        Assert.AreEqual(2, result.Relocations.Count);
        StringAssert.Contains(result.Reason, "2 个候选");
    }

    /// <summary>
    /// 核心用例：位置相关 id（回退形态含 @span）即使给了候选也**不得**可重定位 ——
    /// 否则会在文件前部改动后"重新定位"到错误位置且看起来成功。
    /// </summary>
    [TestMethod]
    public void PositionDependentId_IsNeverRelocatable_EvenWithCandidate()
    {
        var result = SymbolRefFreshness.Evaluate(
            Reference("Method:(T:Ns.Type)Run@1234"), CurrentHash, new[] { Candidate(77) });

        Assert.AreEqual(SymbolRefState.Stale, result.State);
        Assert.IsFalse(result.CanRelocate);
        Assert.IsEmpty(result.Relocations);
        StringAssert.Contains(result.Reason, "@span");
    }

    /// <summary>形态无法判定时按不可重定位处理（保守优先）。</summary>
    [TestMethod]
    public void UnknownSymbolIdForm_IsTreatedAsNotRelocatable()
    {
        var result = SymbolRefFreshness.Evaluate(
            Reference("py:module.func"), CurrentHash, new[] { Candidate(41) });

        Assert.AreEqual(SymbolRefState.Stale, result.State);
        Assert.IsFalse(result.CanRelocate);
        StringAssert.Contains(result.Reason, "无法判定");
    }

    /// <summary>当前版本未知（文件未索引）时**不能**当作 Fresh —— 无法证明一致就不算一致。</summary>
    [TestMethod]
    public void UnknownCurrentHash_IsStale_NotFresh()
    {
        foreach (var unknown in new[] { null, "", "   " })
        {
            var result = SymbolRefFreshness.Evaluate(Reference(), unknown, null);

            Assert.AreEqual(SymbolRefState.Stale, result.State, "未知版本不得判为 Fresh。");
            StringAssert.Contains(result.Reason, "无法证明");
        }
    }

    /// <summary>结果类型自身的不变量：Fresh 不带候选；Stale 必须有理由。</summary>
    [TestMethod]
    public void ResultInvariants_AreEnforcedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => SymbolRefFreshnessResult.Stale("  ", null));
        Assert.IsEmpty(SymbolRefFreshnessResult.Fresh().Relocations);
        Assert.IsFalse(SymbolRefFreshnessResult.Stale("x", new[] { Candidate(1), Candidate(2) }).CanRelocate);
    }
}
