using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 变更类后置条件（设计方案 §1.1 修正 + §4.2）：
/// <b>不得</b>按动作名推断导航。<c>fill</c> 只改 value、<c>click</c> 只开菜单、SPA 内部更新
/// 都不会推进版本，这些都必须判成功；只有调用方**显式声明**的文档导航期望未满足时才失败。
/// </summary>
[TestClass]
public sealed class BrowserMutationPostconditionTests
{
    private static readonly DesktopPageVersion Pinned = new(7);

    private static BrowserMutationOutcome Outcome(
        DesktopPageVersion observed,
        BrowserMutationExpectation expectation = BrowserMutationExpectation.None,
        bool navigationObserved = false,
        BrowserAutomationOperation operation = BrowserAutomationOperation.Interact) =>
        new(operation, Pinned, observed, expectation, navigationObserved);

    [TestMethod]
    public void Fill_That_Leaves_The_Document_Generation_Unchanged_Is_Accepted()
    {
        // 实测反例：fill 只改 input value，pageVersion 仍为 7 —— 旧规则把它判成 internal_error。
        Assert.IsNull(BrowserMutationPostcondition.Validate(Outcome(Pinned)));
    }

    [TestMethod]
    public void Click_That_Only_Opens_A_Menu_Is_Accepted()
    {
        // click 只打开菜单/触发 SPA 更新：没有新文档，也没有版本推进。
        Assert.IsNull(BrowserMutationPostcondition.Validate(Outcome(Pinned)));
    }

    [TestMethod]
    public void Version_Advance_Is_Accepted_For_Any_Expectation()
    {
        Assert.IsNull(BrowserMutationPostcondition.Validate(Outcome(new DesktopPageVersion(8))));
        Assert.IsNull(BrowserMutationPostcondition.Validate(
            Outcome(new DesktopPageVersion(8), BrowserMutationExpectation.DocumentNavigation)));
    }

    [TestMethod]
    public void Observed_Navigation_Is_Accepted_Even_When_The_Version_Did_Not_Advance()
    {
        // 导航竞态：动作后读到旧版本，但驱动确实观测到了导航。
        Assert.IsNull(BrowserMutationPostcondition.Validate(
            Outcome(Pinned, BrowserMutationExpectation.DocumentNavigation, navigationObserved: true)));
    }

    [TestMethod]
    public void Declared_Navigation_Without_Observed_Commit_Is_Reported_As_Unknown_Outcome()
    {
        var error = BrowserMutationPostcondition.Validate(
            Outcome(Pinned, BrowserMutationExpectation.DocumentNavigation));

        Assert.IsNotNull(error);
        Assert.AreEqual(DesktopCapabilityErrorCode.OutcomeUnknown, error!.Code);
        Assert.IsTrue(error.MayHaveSideEffects, "the input was committed even though no new document appeared");
        Assert.IsFalse(error.IsSafeToRetry, "an unknown outcome must never be retried automatically");
    }

    [TestMethod]
    public void Missing_Live_Version_Is_A_Real_Failure()
    {
        // 引用必须带活版本：没有活版本时旧引用无法作废、新引用无法建立。
        var error = BrowserMutationPostcondition.Validate(Outcome(DesktopPageVersion.Unknown));

        Assert.IsNotNull(error);
        Assert.AreEqual(DesktopCapabilityErrorCode.InternalError, error!.Code);
    }

    [TestMethod]
    public void Navigate_With_A_Document_Commit_Expectation_Is_Accepted_On_Advance()
    {
        Assert.IsNull(BrowserMutationPostcondition.Validate(Outcome(
            new DesktopPageVersion(9),
            BrowserMutationExpectation.DocumentNavigation,
            operation: BrowserAutomationOperation.Navigate)));
    }

    [TestMethod]
    public void Unknown_Operation_Or_Expectation_Is_Rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserMutationPostcondition.Validate(
                Outcome(Pinned, operation: (BrowserAutomationOperation)99)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserMutationPostcondition.Validate(
                Outcome(Pinned, (BrowserMutationExpectation)99)));
    }

    // ── 标签页：不拿另一页的版本推进当证据 ────────────────────────────────

    [TestMethod]
    public void Tab_Close_Is_Judged_By_Whether_The_Tab_Closed_Not_By_The_Version()
    {
        // 关闭非最后一页：版本没有理由推进，但确实关掉了。
        Assert.IsNull(BrowserMutationPostcondition.ValidateTabs(
            DesktopTabAction.Close, tabClosed: true, observedVersion: Pinned));

        var error = BrowserMutationPostcondition.ValidateTabs(
            DesktopTabAction.Close, tabClosed: false, observedVersion: Pinned);
        Assert.IsNotNull(error);
        Assert.AreEqual(DesktopCapabilityErrorCode.InternalError, error!.Code);
    }

    [TestMethod]
    public void Closing_The_Last_Page_Is_Accepted_Without_Any_Remaining_Version()
    {
        // 关掉最后一页后没有剩余页面 ⇒ 没有活版本是正常的（旧规则会在这里假失败）。
        Assert.IsNull(BrowserMutationPostcondition.ValidateTabs(
            DesktopTabAction.Close, tabClosed: true, observedVersion: DesktopPageVersion.Unknown));
    }

    [TestMethod]
    public void Tab_Activate_And_New_Require_A_Live_Version()
    {
        Assert.IsNull(BrowserMutationPostcondition.ValidateTabs(
            DesktopTabAction.Activate, tabClosed: false, observedVersion: Pinned));
        Assert.IsNull(BrowserMutationPostcondition.ValidateTabs(
            DesktopTabAction.New, tabClosed: false, observedVersion: Pinned));

        var error = BrowserMutationPostcondition.ValidateTabs(
            DesktopTabAction.Activate, tabClosed: false, observedVersion: DesktopPageVersion.Unknown);
        Assert.IsNotNull(error);
        Assert.AreEqual(DesktopCapabilityErrorCode.InternalError, error!.Code);
    }

    [TestMethod]
    public void Unknown_Tab_Action_Is_Rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserMutationPostcondition.ValidateTabs(
                (DesktopTabAction)99, tabClosed: false, observedVersion: Pinned));
    }
}
