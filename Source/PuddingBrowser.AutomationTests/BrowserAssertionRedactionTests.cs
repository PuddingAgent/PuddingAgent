using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 断言脱敏（设计方案 §4.1）：回执里的 actual 必须有长度上限；敏感字段（密码等）
/// 只允许用 matched + length 表达；日志摘要不含任何值。
/// </summary>
[TestClass]
public sealed class BrowserAssertionRedactionTests
{
    [TestMethod]
    public void Non_Sensitive_Value_Is_Bounded_And_Flagged_When_Truncated()
    {
        var longValue = new string('x', BrowserActionAssertion.MaxActualLength + 40);

        var assertion = B0Fixture.Assertion(BrowserAssertionKind.Value, true, longValue);

        Assert.AreEqual(BrowserActionAssertion.MaxActualLength, assertion.Actual!.Length);
        Assert.AreEqual(longValue.Length, assertion.RawLength);
        Assert.IsTrue(assertion.IsTruncated, "truncation must be visible, not silent");
        Assert.IsFalse(assertion.IsSensitive);
    }

    [TestMethod]
    public void Short_Value_Is_Kept_Verbatim()
    {
        var assertion = B0Fixture.Assertion(BrowserAssertionKind.Value, true, "Pudding");

        Assert.AreEqual("Pudding", assertion.Actual);
        Assert.IsFalse(assertion.IsTruncated);
        Assert.IsFalse(assertion.IsSensitive);
    }

    [TestMethod]
    public void Sensitive_Value_Never_Leaves_Any_Plaintext()
    {
        const string secret = "correct-horse-battery-staple";

        var assertion = B0Fixture.Assertion(BrowserAssertionKind.Value, true, secret, sensitive: true);

        Assert.IsTrue(assertion.IsSensitive);
        Assert.IsNull(assertion.Actual, "a sensitive value must not be echoed in any form");
        Assert.AreEqual(secret.Length, assertion.RawLength, "matches are still provable via matched + length");
        Assert.IsTrue(assertion.Matched);

        var rendered = assertion.ToString();
        Assert.IsFalse(rendered.Contains("correct", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("staple", StringComparison.Ordinal));
        StringAssert.Contains(rendered, "sensitive");
    }

    [TestMethod]
    public void Audit_Summary_Carries_Only_Kind_And_Outcome()
    {
        var assertion = B0Fixture.Assertion(BrowserAssertionKind.Checked, false, "true");

        var summary = assertion.ToAuditSummary();

        Assert.AreEqual("checked=not_matched", summary);
        Assert.IsFalse(summary.Contains("true", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Missing_Actual_Is_Represented_As_Unknown_Not_As_An_Empty_Value()
    {
        var assertion = new BrowserActionAssertion(BrowserAssertionKind.ElementState, true);

        Assert.IsNull(assertion.Actual);
        Assert.IsNull(assertion.RawLength);
        Assert.IsFalse(assertion.IsTruncated);
    }

    [TestMethod]
    public void Control_Characters_In_Actual_Are_Stripped()
    {
        var assertion = B0Fixture.Assertion(BrowserAssertionKind.Value, true, "line1\u0000\r\nline2");

        Assert.IsFalse(assertion.Actual!.Contains('\u0000'));
    }

    [TestMethod]
    public void Unregistered_Assertion_Kind_Is_Rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new BrowserActionAssertion((BrowserAssertionKind)99, true));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserAssertionKindWire.NameOf((BrowserAssertionKind)99));
    }

    [TestMethod]
    public void Assertion_Kind_Wire_Names_Are_Stable()
    {
        Assert.AreEqual("value", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.Value));
        Assert.AreEqual("checked", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.Checked));
        Assert.AreEqual("selected_values", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.SelectedValues));
        Assert.AreEqual("url", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.Url));
        Assert.AreEqual("text_present", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.TextPresent));
        Assert.AreEqual("element_state", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.ElementState));
        Assert.AreEqual("scroll_offset", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.ScrollOffset));
        Assert.AreEqual("focus", BrowserAssertionKindWire.NameOf(BrowserAssertionKind.Focus));

        foreach (var kind in Enum.GetValues<BrowserAssertionKind>())
        {
            Assert.IsTrue(BrowserAssertionKindWire.TryParse(BrowserAssertionKindWire.NameOf(kind), out var parsed));
            Assert.AreEqual(kind, parsed);
        }

        Assert.IsFalse(BrowserAssertionKindWire.TryParse("brand_new_kind", out _));
    }

    [TestMethod]
    public void A_Verified_Receipt_Can_Prove_A_Password_Fill_Without_Echoing_It()
    {
        const string password = "hunter2-hunter2";

        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.Value, true, password, sensitive: true)],
            requiresAssertions: true));

        Assert.IsTrue(receipt.IsVerified, "matched=true + length is enough to verify a password fill");
        Assert.IsNull(receipt.Assertions[0].Actual);
        Assert.IsFalse(receipt.ToString().Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Observation_Stamp_Requires_Utc()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new BrowserObservationStamp(
                new DesktopPageVersion(1),
                new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(8))));
    }

    [TestMethod]
    public void Execution_Verification_And_Completion_Wire_Names_Are_Stable()
    {
        Assert.AreEqual("not_started", BrowserActionExecutionWire.NameOf(BrowserActionExecution.NotStarted));
        Assert.AreEqual("dispatched", BrowserActionExecutionWire.NameOf(BrowserActionExecution.Dispatched));
        Assert.AreEqual("completed", BrowserActionExecutionWire.NameOf(BrowserActionExecution.Completed));
        Assert.AreEqual("unknown", BrowserActionExecutionWire.NameOf(BrowserActionExecution.Unknown));

        Assert.AreEqual("not_requested", BrowserActionVerificationWire.NameOf(BrowserActionVerification.NotRequested));
        Assert.AreEqual("passed", BrowserActionVerificationWire.NameOf(BrowserActionVerification.Passed));
        Assert.AreEqual("failed", BrowserActionVerificationWire.NameOf(BrowserActionVerification.Failed));
        Assert.AreEqual("unverified", BrowserActionVerificationWire.NameOf(BrowserActionVerification.Unverified));

        Assert.AreEqual("verified", BrowserActionCompletionWire.NameOf(BrowserActionCompletion.Verified));
        Assert.AreEqual("dispatched", BrowserActionCompletionWire.NameOf(BrowserActionCompletion.Dispatched));
        Assert.AreEqual("blocked", BrowserActionCompletionWire.NameOf(BrowserActionCompletion.Blocked));
        Assert.AreEqual("failed", BrowserActionCompletionWire.NameOf(BrowserActionCompletion.Failed));
        Assert.AreEqual("unknown", BrowserActionCompletionWire.NameOf(BrowserActionCompletion.Unknown));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserActionExecutionWire.NameOf((BrowserActionExecution)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserActionVerificationWire.NameOf((BrowserActionVerification)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserActionCompletionWire.NameOf((BrowserActionCompletion)99));
    }
}
