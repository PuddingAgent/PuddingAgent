using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 页面脚本对话框的策略（§8 可靠性）：未处理会把页面**卡住**，因此必须确定性作答；
/// 同时**绝不编造用户输入**。
/// </summary>
[TestClass]
public sealed class ScriptDialogPolicyTests
{
    [TestMethod]
    public void AlertIsAcceptedBecauseItHasNoOtherAnswer()
    {
        Assert.AreEqual(
            ScriptDialogPolicy.Answer.Accept, ScriptDialogPolicy.Decide(ScriptDialogPolicy.Kind.Alert));
    }

    [TestMethod]
    public void ConfirmAndPromptAreDismissed_NeverAssumedAffirmative()
    {
        // 肯定可能触发不可逆操作 ⇒ 安全侧是否定；prompt 更不能替用户填内容。
        Assert.AreEqual(
            ScriptDialogPolicy.Answer.Dismiss, ScriptDialogPolicy.Decide(ScriptDialogPolicy.Kind.Confirm));
        Assert.AreEqual(
            ScriptDialogPolicy.Answer.Dismiss, ScriptDialogPolicy.Decide(ScriptDialogPolicy.Kind.Prompt));
    }

    [TestMethod]
    public void PromptNeverGetsTextBack()
    {
        Assert.IsFalse(ScriptDialogPolicy.ShouldProvideText(ScriptDialogPolicy.Kind.Prompt));
        Assert.IsFalse(ScriptDialogPolicy.ShouldProvideText(ScriptDialogPolicy.Kind.Alert));
        Assert.IsFalse(ScriptDialogPolicy.ShouldProvideText(ScriptDialogPolicy.Kind.Confirm));
    }

    [TestMethod]
    public void SummaryIsBoundedAndSingleLine()
    {
        var message = "line1\nline2\t\u0007" + new string('x', 500);
        var summary = ScriptDialogPolicy.Describe(
            ScriptDialogPolicy.Kind.Confirm, message, ScriptDialogPolicy.Answer.Dismiss);

        Assert.Contains("kind=confirm", summary);
        Assert.Contains("answered=dismiss", summary);
        Assert.Contains("messageChars=", summary);

        // 单行、无控制字符、且有长度上限（日志/回执里不该出现换行或整页内容）。
        Assert.IsFalse(summary.Contains('\n'));
        Assert.IsFalse(summary.Contains('\t'));
        Assert.IsFalse(summary.Contains('\u0007'));
        Assert.IsTrue(summary.Length < 300, $"摘要过长：{summary.Length}");
        Assert.Contains("…", summary);
    }

    [TestMethod]
    public void SummaryWithoutMessageOmitsTheSnippet()
    {
        var summary = ScriptDialogPolicy.Describe(
            ScriptDialogPolicy.Kind.Alert, null, ScriptDialogPolicy.Answer.Accept);

        Assert.Contains("kind=alert", summary);
        Assert.Contains("messageChars=0", summary);
        Assert.IsFalse(summary.Contains("message="));
    }

    [TestMethod]
    public void NormalizeCollapsesWhitespaceAndTrims()
    {
        Assert.AreEqual("a b c", ScriptDialogPolicy.Normalize("  a\n\n b\tc  "));
        Assert.AreEqual(string.Empty, ScriptDialogPolicy.Normalize(null));
        Assert.AreEqual(string.Empty, ScriptDialogPolicy.Normalize("   \n\t "));
    }
}
