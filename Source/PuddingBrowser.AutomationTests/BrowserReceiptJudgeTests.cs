using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// G3 回执门禁（设计方案 §4.1/§4.2 / §11）：
/// 按动作验证、按事实等待 —— 不因「版本没推进」把菜单点击判成失败，
/// 也不把「已派发但未验证」升级成 verified。
/// </summary>
[TestClass]
public sealed class BrowserReceiptJudgeTests
{
    [TestMethod]
    public void Same_Document_Fill_Is_Verified_When_The_Value_Reads_Back()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.Value, true, "Pudding")],
            requiresAssertions: true));

        Assert.AreEqual(BrowserActionCompletion.Verified, receipt.Completion);
        Assert.AreEqual(BrowserActionVerification.Passed, receipt.Verification);
        Assert.IsTrue(receipt.Ok);
        Assert.IsFalse(receipt.SafeToRetry, "a verified action must never be retried");
        Assert.IsFalse(receipt.RequiresFollowUp);
        Assert.AreEqual("Pudding", receipt.Assertions[0].Actual);
    }

    [TestMethod]
    public void Click_That_Only_Opens_A_Menu_Is_Dispatched_Not_Failed()
    {
        // 关键修正：不得按动作名推断导航。click 没有业务 expect ⇒ dispatched，而不是 failed。
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Dispatched));

        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.AreEqual(BrowserActionVerification.NotRequested, receipt.Verification);
        Assert.IsTrue(receipt.Ok, "an explicitly dispatched action is reported as ok for the caller");
        Assert.IsTrue(receipt.RequiresFollowUp);
        Assert.IsFalse(receipt.SafeToRetry, "a dispatched action must not be retried blindly");
        Assert.IsTrue(receipt.MayHaveSideEffects);
    }

    [TestMethod]
    public void Click_With_A_Satisfied_Expect_Is_Verified_Even_Without_A_Version_Advance()
    {
        // 菜单/SPA 更新：URL 变了即 expect 通过，PageVersion 不需要推进。
        var evidence = B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.ElementState, true, "menu-open")],
            requiresAssertions: true) with
        {
            Effects = new BrowserActionEffectSummary
            {
                UrlBefore = new Uri("https://example.com/"),
                UrlAfter = new Uri("https://example.com/#menu"),
            },
        };

        var receipt = BrowserReceiptJudge.Judge(evidence);

        Assert.IsTrue(receipt.IsVerified);
        Assert.IsTrue(receipt.Effects.NavigationObserved);
        Assert.AreEqual(
            receipt.Before!.PageVersion.Value,
            receipt.After!.PageVersion.Value,
            "a verified click may leave the committed document generation unchanged (menu / SPA update)");
    }

    [TestMethod]
    public void Navigation_Completed_Is_Verified_With_A_Url_Expectation()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Navigate,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.Url, true, "https://iana.org/")],
            requiresAssertions: true));

        Assert.IsTrue(receipt.IsVerified);
        Assert.IsTrue(receipt.MayHaveSideEffects);
        Assert.IsFalse(receipt.SafeToRetry);
    }

    [TestMethod]
    public void Failed_Expectation_Is_A_Failure_Even_Though_The_Input_Was_Dispatched()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.Value, false, "expected-x")],
            requiresAssertions: true));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.AreEqual(BrowserActionVerification.Failed, receipt.Verification);
        Assert.IsFalse(receipt.Ok);
        Assert.AreEqual(1, receipt.FailedAssertions.Count);
        Assert.IsFalse(receipt.SafeToRetry, "the input was already committed; retrying is not safe");
        Assert.IsTrue(receipt.MayHaveSideEffects);
    }

    [TestMethod]
    public void Timeout_After_Dispatch_Is_Dispatched_With_An_Unverified_Assertion()
    {
        // 方案 §4.2：超时且输入已提交 ⇒ 如实标注「已提交但验证不了」，不能标成可安全重试。
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            requiresAssertions: true,
            unableToVerify: true,
            error: DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: true)));

        Assert.AreEqual(BrowserActionVerification.Unverified, receipt.Verification);
        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.IsFalse(receipt.SafeToRetry, "a committed input is not safe to retry");
        Assert.IsTrue(receipt.MayHaveSideEffects);
        Assert.IsTrue(receipt.RequiresFollowUp);
    }

    [TestMethod]
    public void Cancellation_Before_Dispatch_Is_Safe_To_Retry()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Navigate,
            BrowserActionExecution.NotStarted,
            error: DesktopCapabilityError.Cancelled(mayHaveSideEffects: false)));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.IsFalse(receipt.Ok);
        Assert.IsTrue(receipt.SafeToRetry, "nothing was dispatched, so a retry cannot duplicate an effect");
        Assert.IsFalse(receipt.MayHaveSideEffects);
    }

    [TestMethod]
    public void Gate_Refusal_Is_Blocked_With_No_Side_Effects()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.NotStarted,
            isBlocked: true,
            error: new DesktopCapabilityError(DesktopCapabilityErrorCode.UserTakeover)));

        Assert.AreEqual(BrowserActionCompletion.Blocked, receipt.Completion);
        Assert.IsFalse(receipt.Ok);
        Assert.IsFalse(receipt.MayHaveSideEffects, "a refused action never touched the page");
    }

    [TestMethod]
    public void Disconnect_Leaves_The_Outcome_Unknown()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Navigate,
            BrowserActionExecution.Unknown,
            error: DesktopCapabilityError.OutcomeUnknown("connection dropped mid-flight")));

        Assert.AreEqual(BrowserActionCompletion.Unknown, receipt.Completion);
        Assert.IsFalse(receipt.Ok);
        Assert.IsTrue(receipt.MayHaveSideEffects);
        Assert.IsFalse(receipt.SafeToRetry, "an unknown outcome must never be retried automatically");
    }

    [TestMethod]
    public void Select_NoOp_That_Was_Already_Satisfied_Is_Verified()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.SelectedValues, true, "b")],
            requiresAssertions: true));

        Assert.IsTrue(receipt.IsVerified, "an already-satisfied state is success, not a no-op failure");
    }

    [TestMethod]
    public void Scroll_At_Boundary_Is_Verified_When_Offsets_Are_Reported()
    {
        var evidence = B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.ScrollOffset, true, "1024")],
            requiresAssertions: true) with
        {
            Effects = new BrowserActionEffectSummary
            {
                ScrollOffsetBefore = 1024,
                ScrollOffsetAfter = 1024,
                ScrollAtBoundary = true,
            },
        };

        var receipt = BrowserReceiptJudge.Judge(evidence);

        Assert.IsTrue(receipt.IsVerified);
        Assert.IsTrue(receipt.Effects.ScrollAtBoundary);
        Assert.IsFalse(receipt.Effects.ScrollMoved, "a boundary no-op is not movement");
    }

    [TestMethod]
    public void Zero_Displacement_Must_Not_Be_Reported_As_Movement()
    {
        var effects = new BrowserActionEffectSummary
        {
            ScrollOffsetBefore = 200,
            ScrollOffsetAfter = 200,
        };

        Assert.IsFalse(effects.ScrollMoved);
        Assert.AreEqual(0d, effects.ScrollDelta);
    }

    [TestMethod]
    public void Required_Assertions_Without_Evidence_Are_Unverified_Not_Failed()
    {
        var verification = BrowserReceiptJudge.JudgeVerification(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            requiresAssertions: true));

        Assert.AreEqual(BrowserActionVerification.Unverified, verification);
    }

    [TestMethod]
    public void Completed_Without_Any_Expectation_Is_Dispatched_Not_Verified()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed));

        Assert.AreEqual(BrowserActionVerification.NotRequested, receipt.Verification);
        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.IsFalse(receipt.IsVerified, "verified requires a satisfied business assertion");
    }

    [TestMethod]
    public void Not_Started_With_A_Retryable_Fault_Is_Safe_To_Retry()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.NotStarted,
            error: DesktopCapabilityError.Cancelled(mayHaveSideEffects: false)));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.AreEqual(BrowserActionVerification.NotRequested, receipt.Verification);
        Assert.IsTrue(receipt.SafeToRetry, "not_started plus a retryable fault is the only auto-retry case");
    }

    [TestMethod]
    public void Safe_To_Retry_Requires_Not_Started_Even_When_The_Fault_Is_Retryable()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            error: DesktopCapabilityError.Cancelled(mayHaveSideEffects: false)));

        Assert.IsFalse(receipt.SafeToRetry);
    }

    [TestMethod]
    public void Ok_Is_False_For_Unknown_Blocked_And_Failed()
    {
        var blocked = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.NotStarted,
            isBlocked: true,
            error: new DesktopCapabilityError(DesktopCapabilityErrorCode.Paused)));

        var failed = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.Value, false, "wrong")],
            requiresAssertions: true));

        Assert.AreEqual(BrowserActionCompletion.Blocked, blocked.Completion);
        Assert.AreEqual(BrowserActionCompletion.Failed, failed.Completion);
        Assert.IsFalse(blocked.Ok);
        Assert.IsFalse(failed.Ok);

        foreach (var execution in new[]
                 {
                     BrowserActionExecution.NotStarted,
                     BrowserActionExecution.Unknown,
                 })
        {
            var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
                BrowserAutomationOperation.Interact, execution));

            Assert.IsFalse(receipt.Ok, $"{execution} must not be reported as ok");
        }
    }

    [TestMethod]
    public void Ok_Is_True_For_Verified_And_Dispatched()
    {
        var verified = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Completed,
            assertions: [B0Fixture.Assertion(BrowserAssertionKind.Value, true, "x")],
            requiresAssertions: true));

        var dispatched = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Interact,
            BrowserActionExecution.Dispatched));

        Assert.IsTrue(verified.Ok);
        Assert.IsTrue(dispatched.Ok);
    }

    [TestMethod]
    public void Read_Only_Operation_Does_Not_Claim_Side_Effects()
    {
        var receipt = BrowserReceiptJudge.Judge(B0Fixture.Evidence(
            BrowserAutomationOperation.Snapshot,
            BrowserActionExecution.Unknown));

        Assert.IsFalse(receipt.MayHaveSideEffects, "a read cannot have side effects");
        Assert.AreEqual(BrowserActionCompletion.Unknown, receipt.Completion);
    }

    [TestMethod]
    public void Receipt_Rejects_A_Non_Completed_Verification_Combination()
    {
        // 「执行完成 + 断言通过」是 verified 的唯一前提；构造入参必须登记合法枚举。
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => B0Fixture.Receipt(
            execution: BrowserActionExecution.Completed,
            verification: (BrowserActionVerification)99));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => B0Fixture.Receipt(
            execution: (BrowserActionExecution)99));
    }

    [TestMethod]
    public void Receipt_Carries_The_Operation_And_Page_Identity()
    {
        var operationId = OperationId.NewId();
        var receipt = B0Fixture.Receipt(operationId: operationId);

        Assert.AreEqual(operationId.Value, receipt.OperationId.Value);
        Assert.AreEqual(B0Fixture.Page().Key, receipt.Target!.Key);
        StringAssert.Contains(receipt.ToString(), operationId.Value);
    }
}
