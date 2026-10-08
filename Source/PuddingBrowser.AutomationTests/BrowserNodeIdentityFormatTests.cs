using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 引用的宿主绑定身份与判定（设计方案 §1.1/§5：禁止自动重绑）。
/// 重点是把「证明相同」（Valid）与「无法证明」（Unverifiable）与「确实换了节点」（Replaced）分开。
/// </summary>
[TestClass]
public sealed class BrowserNodeIdentityTests
{
    private static BrowserNodeIdentity.Identity Id(
        string page = "a1b2", long version = 3, int index = 7, long? backend = 42) =>
        new(page, version, index, backend);

    [TestMethod]
    public void EncodeAndParse_RoundTripWithAndWithoutHostIdentity()
    {
        var withHost = Id();
        Assert.AreEqual("pa1b2-v3-n7-b42", withHost.Encode());
        Assert.IsTrue(BrowserNodeIdentity.TryParse(withHost.Encode(), out var parsed));
        Assert.AreEqual(withHost, parsed);

        // 旧引用（没有宿主身份）必须能被解析出来，且身份**如实为 null**（不猜、不补 0）。
        var legacy = Id(backend: null);
        Assert.AreEqual("pa1b2-v3-n7", legacy.Encode());
        Assert.IsTrue(BrowserNodeIdentity.TryParse(legacy.Encode(), out var parsedLegacy));
        Assert.IsNull(parsedLegacy.BackendNodeId);
        Assert.IsFalse(parsedLegacy.HasHostIdentity);
    }

    [TestMethod]
    public void MalformedRefs_AreRejectedInsteadOfGuessed()
    {
        foreach (var value in new[] { null, "", "   ", "p-v3-n7", "a1b2-v3-n7", "pa1b2-x3-n7", "pa1b2-v3-m7",
            "pa1b2-v3-n7-b", "pa1b2-v3-n7-b0", "pa1b2-v3-n7-b42-extra" })
        {
            Assert.IsFalse(BrowserNodeIdentity.TryParse(value, out _), $"'{value}' 不应被解析成功");
        }
    }

    [TestMethod]
    public void SamePageVersionIndexAndNode_IsValid()
    {
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.Valid,
            BrowserNodeIdentity.Judge(Id(), Id()));
        Assert.IsTrue(BrowserNodeIdentity.IsSafeToProceed(BrowserNodeIdentity.Verdict.Valid));
        Assert.IsNull(BrowserNodeIdentity.ErrorCodeOf(BrowserNodeIdentity.Verdict.Valid));
    }

    [TestMethod]
    public void SamePositionButDifferentNode_IsReplaced_AndMustBeRejected()
    {
        // SPA 原地重渲染：版本没动、序号没动，但同一位置已经是另一个节点。
        // 这是"错位读取"的真实来源——**必须拒绝**，不能因为版本相同就放行。
        var verdict = BrowserNodeIdentity.Judge(Id(backend: 42), Id(backend: 43));

        Assert.AreEqual(BrowserNodeIdentity.Verdict.Replaced, verdict);
        Assert.IsFalse(BrowserNodeIdentity.IsSafeToProceed(verdict));
        Assert.AreEqual("stale_element_reference", BrowserNodeIdentity.ErrorCodeOf(verdict));
        StringAssert.Contains(BrowserNodeIdentity.Describe(verdict), "replaced");
    }

    [TestMethod]
    public void DifferentIndexAtTheSameVersion_IsTreatedAsReplaced()
    {
        // 同页同版却对不上序号 ⇒ 序号本身不可信（定位结果变了），与"节点被换"同义。
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.Replaced,
            BrowserNodeIdentity.Judge(Id(index: 7), Id(index: 8)));
    }

    [TestMethod]
    public void VersionOrPageMismatch_KeepsTheExistingStaleSemantics()
    {
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.StaleVersion,
            BrowserNodeIdentity.Judge(Id(version: 3), Id(version: 4)));
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.WrongPage,
            BrowserNodeIdentity.Judge(Id(page: "a1b2"), Id(page: "c3d4")));

        Assert.AreEqual("stale_element_reference", BrowserNodeIdentity.ErrorCodeOf(BrowserNodeIdentity.Verdict.StaleVersion));
        Assert.AreEqual("stale_element_reference", BrowserNodeIdentity.ErrorCodeOf(BrowserNodeIdentity.Verdict.WrongPage));
    }

    [TestMethod]
    public void MissingHostIdentity_IsUnverifiable_NotSilentlyValid()
    {
        // 旧引用没有宿主身份：**不能声称相同**，也不能凭空判死 —— 如实报"无法证明"，
        // 且提示要求"要确定就重新观察"。
        var verdict = BrowserNodeIdentity.Judge(Id(backend: null), Id(backend: 42));

        Assert.AreEqual(BrowserNodeIdentity.Verdict.Unverifiable, verdict);
        Assert.IsTrue(BrowserNodeIdentity.IsSafeToProceed(verdict));
        Assert.IsNull(BrowserNodeIdentity.ErrorCodeOf(verdict));
        StringAssert.Contains(BrowserNodeIdentity.Describe(verdict), "neither confirmed nor rejected");

        // 两侧都没身份同样是 Unverifiable（而不是 Valid）。
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.Unverifiable,
            BrowserNodeIdentity.Judge(Id(backend: null), Id(backend: null)));
    }

    [TestMethod]
    public void WrongPageWinsOverMissingIdentity()
    {
        // 判定顺序刻意：明确的错误先判，最后才落到"无法证明"——
        // 否则一条指向别的页面的旧引用会被报成 Unverifiable 而蒙混过关。
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.WrongPage,
            BrowserNodeIdentity.Judge(Id(page: "a1b2", backend: null), Id(page: "c3d4", backend: null)));
    }

    [TestMethod]
    public void WithHostIdentity_AppendsOrReplacesButNeverInventsOne()
    {
        Assert.AreEqual("pa1b2-v3-n7-b99", BrowserNodeIdentity.WithHostIdentity("pa1b2-v3-n7", 99));
        Assert.AreEqual("pa1b2-v3-n7-b99", BrowserNodeIdentity.WithHostIdentity("pa1b2-v3-n7-b42", 99));

        // 拿不到身份（null/0）时**原样返回**：不编一个 id 出来。
        Assert.AreEqual("pa1b2-v3-n7", BrowserNodeIdentity.WithHostIdentity("pa1b2-v3-n7", null));
        Assert.AreEqual("pa1b2-v3-n7", BrowserNodeIdentity.WithHostIdentity("pa1b2-v3-n7", 0));

        // 非法输入原样返回（判定交给解析方）。
        Assert.AreEqual("garbage", BrowserNodeIdentity.WithHostIdentity("garbage", 99));
    }

    [TestMethod]
    public void Encode_IsCasePreservingForThePageToken_AndJudgeIgnoresCase()
    {
        // 页面 token 是十六进制，大小写不该造成"换了页面"的误判。
        Assert.AreEqual(
            BrowserNodeIdentity.Verdict.Valid,
            BrowserNodeIdentity.Judge(Id(page: "A1B2"), Id(page: "a1b2")));
    }
}
