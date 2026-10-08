using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 重试裁定（设计方案 §4.3）：只有「确定未开始 + 可重试 + 无副作用」才 safe_to_retry；
/// 已派发先观察；断连是 unknown；被闸门拒绝不自动重试。
/// </summary>
[TestClass]
public sealed class BrowserRetryPolicyTests
{
    [TestMethod]
    public void Verified_Is_Not_Retryable()
    {
        var decision = BrowserRetryPolicy.Decide(B0Fixture.Receipt(
            execution: BrowserActionExecution.Completed,
            verification: BrowserActionVerification.Passed));

        Assert.AreEqual(BrowserRetryDisposition.NotRetryable, decision.Disposition);
        Assert.IsFalse(decision.IsSafeToRetry);
        Assert.AreEqual("not_retryable", decision.WireDisposition);
    }

    [TestMethod]
    public void Dispatched_Is_Observe_Only()
    {
        var decision = BrowserRetryPolicy.Decide(B0Fixture.Receipt(
            execution: BrowserActionExecution.Dispatched,
            verification: BrowserActionVerification.NotRequested,
            mayHaveSideEffects: true));

        Assert.AreEqual(BrowserRetryDisposition.DispatchedObserveOnly, decision.Disposition);
        Assert.IsTrue(decision.MayHaveSideEffects);
        Assert.IsFalse(decision.IsSafeToRetry);
        StringAssert.Contains(decision.Detail!, "observe");
    }

    [TestMethod]
    public void Unknown_Outcome_Is_Never_Retried()
    {
        var decision = BrowserRetryPolicy.Decide(B0Fixture.Receipt(
            execution: BrowserActionExecution.Unknown,
            verification: BrowserActionVerification.NotRequested,
            mayHaveSideEffects: true,
            error: DesktopCapabilityError.OutcomeUnknown("dropped")));

        Assert.AreEqual(BrowserRetryDisposition.UnknownOutcome, decision.Disposition);
        Assert.IsFalse(decision.IsSafeToRetry);
    }

    [TestMethod]
    public void Blocked_Requires_An_Explicit_Re_Admission_Instead_Of_An_Automatic_Retry()
    {
        var decision = BrowserRetryPolicy.Decide(B0Fixture.Receipt(
            execution: BrowserActionExecution.NotStarted,
            verification: BrowserActionVerification.NotRequested,
            retryable: true,
            isBlocked: true,
            error: new DesktopCapabilityError(DesktopCapabilityErrorCode.UserTakeover)));

        Assert.AreEqual(BrowserActionCompletion.Blocked, decision.Receipt!.Completion);
        Assert.AreEqual(BrowserRetryDisposition.NotRetryable, decision.Disposition);
        Assert.IsFalse(
            decision.IsSafeToRetry,
            "auto-retrying a gate refusal would be a bypass around the user's takeover");
        StringAssert.Contains(decision.Detail!, "re-admit");
    }

    [TestMethod]
    public void Failed_But_Not_Started_Is_Safe_To_Retry()
    {
        var decision = BrowserRetryPolicy.Decide(B0Fixture.Receipt(
            execution: BrowserActionExecution.NotStarted,
            verification: BrowserActionVerification.NotRequested,
            retryable: true,
            error: DesktopCapabilityError.Cancelled(mayHaveSideEffects: false)));

        Assert.AreEqual(BrowserRetryDisposition.SafeToRetry, decision.Disposition);
        Assert.IsTrue(decision.IsSafeToRetry);
    }

    [TestMethod]
    public void Failed_With_Possible_Side_Effects_Is_Not_Retryable()
    {
        var decision = BrowserRetryPolicy.Decide(B0Fixture.Receipt(
            execution: BrowserActionExecution.NotStarted,
            verification: BrowserActionVerification.NotRequested,
            mayHaveSideEffects: true,
            retryable: true));

        Assert.AreEqual(BrowserRetryDisposition.NotRetryable, decision.Disposition);
        Assert.IsFalse(decision.IsSafeToRetry);
    }

    [TestMethod]
    public void Decision_Keeps_The_Receipt_And_The_Error()
    {
        var error = DesktopCapabilityError.ResourceExhausted("busy");
        var receipt = B0Fixture.Receipt(
            operation: BrowserAutomationOperation.Navigate,
            execution: BrowserActionExecution.NotStarted,
            verification: BrowserActionVerification.NotRequested,
            error: error);

        var decision = BrowserRetryPolicy.Decide(receipt);

        Assert.AreSame(receipt, decision.Receipt);
        Assert.AreSame(error, decision.Error);
    }

    [TestMethod]
    public void Every_Disposition_Has_A_Stable_Wire_Name()
    {
        var names = BrowserRetryDispositionWire.All
            .Select(BrowserRetryDispositionWire.NameOf)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "not_retryable",
                "safe_to_retry",
                "dispatched_observe_only",
                "unknown_outcome",
                "receipt_expired",
                "operation_conflict",
                "capacity_exhausted",
            },
            names);

        foreach (var disposition in BrowserRetryDispositionWire.All)
        {
            Assert.IsTrue(BrowserRetryDispositionWire.TryParse(
                BrowserRetryDispositionWire.NameOf(disposition), out var parsed));
            Assert.AreEqual(disposition, parsed);
        }

        Assert.IsFalse(BrowserRetryDispositionWire.TryParse("brand_new_disposition", out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserRetryDispositionWire.NameOf((BrowserRetryDisposition)1234));
    }
}
