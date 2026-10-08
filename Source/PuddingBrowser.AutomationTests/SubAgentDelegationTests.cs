using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 子代理委派的**纯判定与收窄**（设计方案 §9 / 门禁 G10）：
/// 范围不得扩大、注入工具必须够用、父结论有界（默认 2 KiB）且不含整页。
/// </summary>
[TestClass]
public sealed class SubAgentDelegationTests
{
    private static readonly BrowserDelegationScope FullParent = new(CanRead: true, CanWrite: true, CanManage: true);

    private static BrowserDelegationEnvelope Envelope(
        BrowserDelegationScope? scope = null,
        string goal = "找出价格并回一条结论",
        string expected = "价格与来源 URL",
        string pageGrant = "grant-1",
        int budget = BrowserDelegation.DefaultResultBudgetBytes,
        IReadOnlyList<string>? injected = null,
        IReadOnlyList<string>? required = null,
        TimeSpan? deadline = null) =>
        new(
            goal,
            scope ?? new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            pageGrant,
            expected,
            budget,
            injected ?? ["browser_navigate", "browser_snapshot", "browser_screenshot"],
            required ?? ["browser_snapshot"],
            deadline ?? TimeSpan.FromMinutes(5));

    [TestMethod]
    public void AWellFormedReadOnlyDelegationIsAccepted()
    {
        Assert.IsTrue(BrowserDelegation.TryValidate(Envelope(), FullParent, out var problems), string.Join("; ", problems));
        Assert.AreEqual(0, problems.Count);
    }

    [TestMethod]
    public void ADelegationWithoutGrantGoalOrDeadlineIsRefused()
    {
        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(goal: "  ", pageGrant: "", deadline: TimeSpan.Zero), FullParent, out var problems));

        StringAssert.Contains(string.Join("; ", problems), "goal");
        StringAssert.Contains(string.Join("; ", problems), "page grant");
        StringAssert.Contains(string.Join("; ", problems), "deadline");
    }

    [TestMethod]
    public void AChildScopeCannotExceedItsParent()
    {
        // 子代理不得扩大父授权：父只能读，子却要写 ⇒ 拒绝。
        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(scope: new BrowserDelegationScope(CanRead: true, CanWrite: true, CanManage: false)),
            new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            out var problems));

        StringAssert.Contains(problems[0], "subset");

        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(scope: new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: true)),
            new BrowserDelegationScope(CanRead: true, CanWrite: false, CanManage: false),
            out _));
    }

    [TestMethod]
    public void InjectedToolsMustCoverTheTask()
    {
        // "父代理能发现七项工具"推不出"子代理可用"（§9）：必需工具必须在注入列表里。
        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(injected: ["browser_snapshot"], required: ["browser_navigate"]),
            FullParent,
            out var problems));

        StringAssert.Contains(problems[0], "browser_navigate");

        // 必需工具为空 ⇒ 无人可执行的任务，同样拒绝。
        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(required: []), FullParent, out var empty));
        StringAssert.Contains(empty[0], "required tool list");
    }

    [TestMethod]
    public void WriteCapableDelegationsMustHaveNavigateAndInteract()
    {
        // 写入类任务缺 navigate/interact ⇒ 拒绝（按**代码事实**：工具面里没有 screenshot 工具）。
        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(
                scope: new BrowserDelegationScope(CanRead: true, CanWrite: true, CanManage: false),
                injected: ["browser_snapshot", "browser_wait_for"],
                required: ["browser_snapshot"]),
            FullParent,
            out var problems));

        var joined = string.Join("; ", problems);
        StringAssert.Contains(joined, "browser_navigate");
        StringAssert.Contains(joined, "browser_interact");

        Assert.IsTrue(BrowserDelegation.TryValidate(
            Envelope(
                scope: new BrowserDelegationScope(CanRead: true, CanWrite: true, CanManage: false),
                injected: ["browser_interact", "browser_navigate", "browser_snapshot"],
                required: ["browser_snapshot"]),
            FullParent,
            out _));
    }

    [TestMethod]
    public void TheParentConclusionBudgetIsBoundedByDesign()
    {
        // 允许声明**更小**的预算；不允许把它抬过 2 KiB。
        Assert.IsTrue(BrowserDelegation.TryValidate(Envelope(budget: 512), FullParent, out _));

        Assert.IsFalse(BrowserDelegation.TryValidate(
            Envelope(budget: BrowserDelegation.MaxResultBudgetBytes + 1), FullParent, out var problems));
        StringAssert.Contains(problems[0], "must not exceed");

        Assert.IsFalse(BrowserDelegation.TryValidate(Envelope(budget: 0), FullParent, out var zero));
        StringAssert.Contains(zero[0], "positive");
    }

    [TestMethod]
    public void ASmallConclusionPassesThroughVerbatim()
    {
        var conclusion = BrowserDelegationConclusionShaper.Shape(
            "价格 ¥199，来源 https://example.com/p/1",
            verificationState: "verified",
            url: "https://example.com/p/1",
            operationId: "op-7");

        Assert.IsFalse(conclusion.Truncated);
        Assert.AreEqual("价格 ¥199，来源 https://example.com/p/1", conclusion.Text);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(conclusion.Text), conclusion.Bytes);
        Assert.AreEqual("verified", conclusion.VerificationState);
        Assert.AreEqual("op-7", conclusion.OperationId);
    }

    [TestMethod]
    public void AnOversizedConclusionIsTruncatedOnACharacterBoundaryAndPointsAtTheArtifact()
    {
        // 中文每字 3 字节：预算 30 字节只能放下 10 个字（不能切出半个字符）。
        var full = new string('价', 100);

        var conclusion = BrowserDelegationConclusionShaper.Shape(
            full,
            verificationState: "verified",
            budgetBytes: 30,
            artifactId: "artifact-delegation-1");

        Assert.IsTrue(conclusion.Truncated);
        Assert.AreEqual(30, conclusion.Bytes);
        Assert.AreEqual(new string('价', 10), conclusion.Text);
        Assert.AreEqual("artifact-delegation-1", conclusion.ArtifactId);
        Assert.IsFalse(conclusion.Text.Contains('\uFFFD'), "截断产生了替换字符");
    }

    [TestMethod]
    public void TruncationWithoutAnArtifactIsStillExplicit()
    {
        // 有溢出但没给 Artifact：文本仍然有界、仍然标记截断（不冒充完整结论）。
        var conclusion = BrowserDelegationConclusionShaper.Shape(
            new string('x', 5000), "unverified", budgetBytes: 128);

        Assert.IsTrue(conclusion.Truncated);
        Assert.IsTrue(conclusion.Bytes <= 128);
        Assert.IsNull(conclusion.ArtifactId);
        Assert.AreEqual("unverified", conclusion.VerificationState);
    }

    [TestMethod]
    public void EmptyOrNullTextIsHandledHonestly()
    {
        var nullText = BrowserDelegationConclusionShaper.Shape(null, "unreported");
        Assert.AreEqual(string.Empty, nullText.Text);
        Assert.AreEqual(0, nullText.Bytes);
        Assert.IsFalse(nullText.Truncated);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserDelegationConclusionShaper.Shape("x", "verified", budgetBytes: 0));
        Assert.ThrowsExactly<ArgumentException>(
            () => BrowserDelegationConclusionShaper.Shape("x", "  "));
    }
}
