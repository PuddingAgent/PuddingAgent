using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="WindowsInputSimulator.EvaluateWindowOwnership"/> 的判定表。
/// <para>
/// 锁定的是「有窗口 ≠ 是目标窗口」这条区分。旧实现只有两个分支
/// （没窗口 → 拒绝 / 有窗口 → 放行），漏掉了第三类：
/// 坐标上确实有窗口，但那是弹窗、系统通知或别的应用。
/// </para>
/// </summary>
[TestClass]
public sealed class WindowOwnershipTests
{
    private static readonly IntPtr Authorized = (IntPtr)0x1111;
    private static readonly IntPtr OtherWindow = (IntPtr)0x2222;

    /// <summary>没窗口且未放行 → 拒绝。</summary>
    [TestMethod]
    public void NoWindowAtPoint_WithoutAllowance_IsRejected()
    {
        var verdict = WindowsInputSimulator.EvaluateWindowOwnership(
            hasWindowAtPoint: false,
            windowAtPointRoot: IntPtr.Zero,
            authorizedRoot: Authorized,
            allowWithoutWindow: false);

        Assert.AreEqual(ClickOutcome.RejectedNoWindowAtPoint, verdict);
    }

    /// <summary>
    /// 没窗口但显式放行 → 入合成层场景，按约定放行。
    /// </summary>
    [TestMethod]
    public void NoWindowAtPoint_WithAllowance_IsAllowed()
    {
        var verdict = WindowsInputSimulator.EvaluateWindowOwnership(
            hasWindowAtPoint: false,
            windowAtPointRoot: IntPtr.Zero,
            authorizedRoot: Authorized,
            allowWithoutWindow: true);

        Assert.IsNull(verdict, "合成层目标本就查不到窗口，显式放行时应通过");
    }

    /// <summary>未指定授权根 → 不检查归属，保持旧行为。</summary>
    [TestMethod]
    public void NoAuthorizedRoot_DoesNotCheckOwnership()
    {
        var verdict = WindowsInputSimulator.EvaluateWindowOwnership(
            hasWindowAtPoint: true,
            windowAtPointRoot: OtherWindow,
            authorizedRoot: null,
            allowWithoutWindow: false);

        Assert.IsNull(verdict, "调用方没指定授权窗口时，不应引入新的拒绝");
    }

    /// <summary>窗口根与授权根一致 → 放行。</summary>
    [TestMethod]
    public void MatchingRoot_IsAllowed()
    {
        var verdict = WindowsInputSimulator.EvaluateWindowOwnership(
            hasWindowAtPoint: true,
            windowAtPointRoot: Authorized,
            authorizedRoot: Authorized,
            allowWithoutWindow: false);

        Assert.IsNull(verdict);
    }

    /// <summary>**核心用例**：有窗口但不是授权目标 → 必须拒绝。</summary>
    [TestMethod]
    public void MismatchedRoot_IsRejectedAsUnexpectedWindow()
    {
        var verdict = WindowsInputSimulator.EvaluateWindowOwnership(
            hasWindowAtPoint: true,
            windowAtPointRoot: OtherWindow,
            authorizedRoot: Authorized,
            allowWithoutWindow: false);

        Assert.AreEqual(ClickOutcome.RejectedUnexpectedWindowAtPoint, verdict,
            "落在别的窗口上必须拒绝 —— 否则调用方看到 Clicked 会误以为点中了");
    }

    /// <summary>
    /// <c>allowWithoutWindow</c> 只放宽「没窗口」，**不能**绕过归属检查。
    /// 这两件事经常被混为一谈：一个是合成层查不到窗口，一个是点错了窗口。
    /// </summary>
    [TestMethod]
    public void Allowance_DoesNotBypassOwnershipCheck()
    {
        var verdict = WindowsInputSimulator.EvaluateWindowOwnership(
            hasWindowAtPoint: true,
            windowAtPointRoot: OtherWindow,
            authorizedRoot: Authorized,
            allowWithoutWindow: true);

        Assert.AreEqual(ClickOutcome.RejectedUnexpectedWindowAtPoint, verdict,
            "放行不等于放弃归属检查");
    }

    /// <summary>
    /// 三种拒绝原因必须互不相同 —— 调用方靠它们区分散障类型，
    /// 合并成同一个值就失去了诊断价值。
    /// </summary>
    [TestMethod]
    public void RejectionReasons_AreDistinct()
    {
        var noWindow = WindowsInputSimulator.EvaluateWindowOwnership(
            false, IntPtr.Zero, Authorized, allowWithoutWindow: false);

        var wrongWindow = WindowsInputSimulator.EvaluateWindowOwnership(
            true, OtherWindow, Authorized, allowWithoutWindow: false);

        Assert.IsNotNull(noWindow);
        Assert.IsNotNull(wrongWindow);
        Assert.AreNotEqual(noWindow, wrongWindow,
            "「没窗口」与「窗口不对」必须是不同的结论");
    }
}
