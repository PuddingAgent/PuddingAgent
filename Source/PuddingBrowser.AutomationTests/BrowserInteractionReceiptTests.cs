using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 交互回执判定（设计方案 §4.1/§4.2、门禁 G7）：
/// 「一次 fill/select/check 调用就能确认成功」靠的是「调用方声明的期望 + 驱动回读的观测值」，
/// 而不是按动作名推断；且**表单值绝不回显**。
/// </summary>
[TestClass]
public sealed class BrowserInteractionReceiptTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    private static BrowserInteractionEvidence Evidence(
        DesktopInteractionAction action,
        string? expectedValue = null,
        bool elementObserved = true,
        string? observedValue = null,
        IReadOnlyList<string>? requestedValues = null,
        IReadOnlyList<string>? observedSelected = null,
        bool? requestedChecked = null,
        bool? observedChecked = null,
        string? requestedText = null,
        BrowserActionExecution execution = BrowserActionExecution.Completed,
        DesktopCapabilityError? error = null,
        string? detail = null) =>
        new(
            OperationId.NewId(),
            action,
            RequestedText: requestedText,
            RequestedValues: requestedValues,
            RequestedChecked: requestedChecked,
            ExpectedValue: expectedValue,
            ElementObserved: elementObserved,
            ObservedValue: observedValue,
            ObservedSelectedValues: observedSelected,
            ObservedChecked: observedChecked,
            Execution: execution,
            Target: Target,
            Error: error,
            Detail: detail);

    // ── G7：一次 fill 即可确认 ────────────────────────────────────────────

    [TestMethod]
    public void Fill_With_ExpectValue_Matching_Is_Verified()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "Pudding",
            observedValue: "Pudding"));

        Assert.IsTrue(receipt.IsVerified);
        Assert.IsTrue(receipt.Ok);
        Assert.AreEqual(BrowserActionVerification.Passed, receipt.Verification);

        var assertion = AssertSingle(receipt);
        Assert.AreEqual(BrowserAssertionKind.Value, assertion.Kind);
        Assert.IsTrue(assertion.Matched);
    }

    [TestMethod]
    public void Fill_Receipt_Never_Echoes_The_Form_Value()
    {
        // 回执必须证明「写进去了」，但不能把输入内容带进工具输出或日志。
        const string secret = "hunter2-do-not-echo";

        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: secret,
            observedValue: secret,
            requestedText: secret));

        var assertion = AssertSingle(receipt);
        Assert.IsTrue(assertion.IsSensitive);
        Assert.IsNull(assertion.Actual, "敏感断言的实际值必须为 null");
        Assert.AreEqual(secret.Length, assertion.RawLength, "用长度表达「写进去了」");
        Assert.IsFalse(receipt.ToString().Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(assertion.ToAuditSummary().Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Fill_With_ExpectValue_Mismatch_Is_Failed_Not_Dispatchable()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "expected",
            observedValue: "actual"));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.IsFalse(receipt.Ok);
        Assert.AreEqual(BrowserActionVerification.Failed, receipt.Verification);
        // 输入已经提交过 ⇒ 不可安全重试，且可能已产生副作用。
        Assert.IsFalse(receipt.SafeToRetry);
        Assert.IsTrue(receipt.MayHaveSideEffects);
    }

    [TestMethod]
    public void Fill_Without_Expectation_Is_Dispatched_Not_Verified()
    {
        // 没声明期望就不假装成功（type 会追加，调用方也没说最终值该是什么）。
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            requestedText: "Pudding",
            observedValue: "Pudding"));

        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.AreEqual(BrowserActionVerification.NotRequested, receipt.Verification);
        Assert.IsTrue(receipt.Ok, "已派发是明确标注的可接受终态");
        Assert.IsTrue(receipt.RequiresFollowUp);
        Assert.AreEqual(0, receipt.Assertions.Count);
    }

    [TestMethod]
    public void Expectation_Declared_Without_Observation_Is_Unverified_Not_Failed()
    {
        // 元素在重渲染中消失：没有证据说它错了 ⇒ unverified。
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "Pudding",
            elementObserved: false));

        Assert.AreEqual(BrowserActionVerification.Unverified, receipt.Verification);
        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.AreEqual(0, receipt.Assertions.Count);
        Assert.IsTrue(receipt.RequiresFollowUp);
    }

    // ── select / check：用调用方自己给的目标状态判 ────────────────────────

    [TestMethod]
    public void Select_Is_Verified_When_The_Observed_Selection_Matches_The_Request()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Select,
            requestedValues: ["b", "a"],
            observedSelected: ["a", "b"]));

        Assert.IsTrue(receipt.IsVerified);
        var assertion = AssertSingle(receipt);
        Assert.AreEqual(BrowserAssertionKind.SelectedValues, assertion.Kind);
        Assert.IsTrue(assertion.Matched);
        Assert.IsTrue(assertion.IsSensitive, "表单值一律不回显");
        Assert.IsNull(assertion.Actual);
    }

    [TestMethod]
    public void Select_Mismatch_Is_Failed()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Select,
            requestedValues: ["a"],
            observedSelected: ["b"]));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.IsFalse(AssertSingle(receipt).Matched);
    }

    [TestMethod]
    public void Check_Is_Verified_And_The_Boolean_May_Be_Shown()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Check,
            requestedChecked: true,
            observedChecked: true));

        var assertion = AssertSingle(receipt);
        Assert.IsTrue(assertion.Matched);
        Assert.AreEqual(BrowserAssertionKind.Checked, assertion.Kind);
        Assert.IsFalse(assertion.IsSensitive, "勾选态是布尔，不含用户输入内容");
        Assert.AreEqual("True", assertion.Actual);
        Assert.IsTrue(receipt.IsVerified);
    }

    [TestMethod]
    public void Check_Already_Satisfied_Is_Still_Verified()
    {
        // 原本就勾上时是 no-op 成功，不是失败（设计方案 §4.2「包括原本已满足的 no-op」）。
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Check,
            requestedChecked: true,
            observedChecked: true));

        Assert.IsTrue(receipt.IsVerified);
    }

    [TestMethod]
    public void Check_Mismatch_Is_Failed()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Check,
            requestedChecked: true,
            observedChecked: false));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
    }

    [TestMethod]
    public void Click_Without_Expectation_Is_Dispatched()
    {
        // 不按动作名推断导航：click 可能只开菜单，如实报 dispatched。
        var receipt = BrowserInteractionReceipt.Build(Evidence(DesktopInteractionAction.Click));

        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.IsTrue(receipt.MayHaveSideEffects);
        Assert.IsFalse(receipt.SafeToRetry);
    }

    [TestMethod]
    public void Scroll_Without_Expectation_Is_Dispatched()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(DesktopInteractionAction.Scroll));

        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
    }

    [TestMethod]
    public void Unregistered_Action_Is_Rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserInteractionReceipt.Build(Evidence((DesktopInteractionAction)99)));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BrowserInteractionReceipt.Build(null!));
    }

    [TestMethod]
    public void The_Receipt_Carries_The_Operation_And_Page_Identity()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "x",
            observedValue: "x"));

        Assert.AreEqual(Target.Key, receipt.Target!.Key);
        Assert.AreEqual(BrowserAutomationOperation.Interact, receipt.Operation);
    }

    // ── 执行阶段推导 ──────────────────────────────────────────────────────

    [TestMethod]
    public void Execution_Stage_Is_Derived_From_The_Error_Taxonomy()
    {
        Assert.AreEqual(
            BrowserActionExecution.Completed,
            BrowserInteractionReceipt.ExecutionFor(null));

        // 明确未开始（准入拒绝、版本不符、目标不存在）：无副作用。
        Assert.AreEqual(
            BrowserActionExecution.NotStarted,
            BrowserInteractionReceipt.ExecutionFor(
                DesktopCapabilityError.Unauthorized("not allowed")));
        Assert.AreEqual(
            BrowserActionExecution.NotStarted,
            BrowserInteractionReceipt.ExecutionFor(
                DesktopCapabilityError.InvalidTarget("gone")));

        // 期限到点：可能已经派发，不得当作未执行。
        Assert.AreEqual(
            BrowserActionExecution.Dispatched,
            BrowserInteractionReceipt.ExecutionFor(
                DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: true)));

        // 结果不明：既不当作未执行，也不自动重试。
        Assert.AreEqual(
            BrowserActionExecution.Unknown,
            BrowserInteractionReceipt.ExecutionFor(
                DesktopCapabilityError.OutcomeUnknown("connection dropped")));
    }

    [TestMethod]
    public void NotStarted_Execution_With_A_Retryable_Fault_Is_Safe_To_Retry()
    {
        // 「未开始 + 故障可重试 + 无副作用」是唯一允许自动重试的组合。
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "x",
            execution: BrowserActionExecution.NotStarted,
            error: DesktopCapabilityError.Cancelled(mayHaveSideEffects: false)));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.IsTrue(receipt.SafeToRetry);
        Assert.IsFalse(receipt.MayHaveSideEffects);
    }

    [TestMethod]
    public void NotStarted_Without_A_Known_Fault_Is_Not_Safe_To_Retry()
    {
        // 没有错误信息 ⇒ 不知道为什么没开始，保守地不自动重试（fail closed）。
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "x",
            execution: BrowserActionExecution.NotStarted));

        Assert.IsFalse(receipt.SafeToRetry);
    }

    // ── 闸门拒绝 vs 失败：语义不同，重试裁定也不同（§3.2/§4.3）───────────────

    [TestMethod]
    public void GateDenials_AreBlocked_AndTheRetryPolicyRefusesAutoRetry()
    {
        // 接管/暂停/授权三者语义一致：未开始、无副作用、**不得自动重试**
        // （自动重试等于绕过用户的接管意图）。
        var denials = new[]
        {
            new DesktopCapabilityError(DesktopCapabilityErrorCode.UserTakeover, "user took over"),
            new DesktopCapabilityError(DesktopCapabilityErrorCode.Paused, "paused"),
            DesktopCapabilityError.Unauthorized("page is not the agent target"),
        };

        foreach (var denial in denials)
        {
            Assert.IsTrue(BrowserInteractionReceipt.IsGateDenial(denial), denial.Code.ToString());

            var receipt = BrowserInteractionReceipt.Build(Evidence(
                DesktopInteractionAction.Fill,
                expectedValue: "x",
                execution: BrowserInteractionReceipt.ExecutionFor(denial),
                error: denial));

            Assert.AreEqual(BrowserActionCompletion.Blocked, receipt.Completion, denial.Code.ToString());
            Assert.IsFalse(receipt.Ok);
            Assert.AreEqual(
                BrowserRetryDisposition.NotRetryable,
                BrowserRetryPolicy.Decide(receipt).Disposition,
                denial.Code.ToString());
        }
    }

    [TestMethod]
    public void OrdinaryFailures_AreNotBlocked_AndStayRetryableWhenNothingWasDispatched()
    {
        var failure = new DesktopCapabilityError(
            DesktopCapabilityErrorCode.PageVersionMismatch, "page moved on");

        Assert.IsFalse(BrowserInteractionReceipt.IsGateDenial(failure));

        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "x",
            execution: BrowserInteractionReceipt.ExecutionFor(failure),
            error: failure));

        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.IsTrue(receipt.SafeToRetry);
        Assert.AreEqual(
            BrowserRetryDisposition.SafeToRetry,
            BrowserRetryPolicy.Decide(receipt).Disposition);
    }

    [TestMethod]
    public void DispatchedThenDeadline_KeepsTheDispatchFact_AndIsNeverRetried()
    {
        // 设计 §4.1 的硬要求：不能丢失"已经派发"的事实。
        // 声明了期望却超时 ⇒ execution 仍是 dispatched（事实），completion 是 failed（断言未满足）。
        var timeout = DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: true);

        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Fill,
            expectedValue: "x",
            execution: BrowserInteractionReceipt.ExecutionFor(timeout),
            error: timeout));

        Assert.AreEqual(BrowserActionExecution.Dispatched, receipt.Execution);
        Assert.AreEqual(BrowserActionCompletion.Failed, receipt.Completion);
        Assert.IsTrue(receipt.MayHaveSideEffects);
        Assert.IsFalse(receipt.SafeToRetry);

        // 已跑过但没通过验证 ⇒ 指引必须说"先观察、勿重做"（不是干巴巴的"不可重试"）。
        var decision = BrowserRetryPolicy.Decide(receipt);
        Assert.AreEqual(BrowserRetryDisposition.DispatchedObserveOnly, decision.Disposition);
        Assert.IsNotNull(decision.Detail);
    }

    [TestMethod]
    public void DispatchedWithoutExpectation_IsObserved_NotClaimedVerified()
    {
        var timeout = DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: true);

        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Click,
            execution: BrowserInteractionReceipt.ExecutionFor(timeout),
            error: timeout));

        Assert.AreEqual(BrowserActionCompletion.Dispatched, receipt.Completion);
        Assert.AreEqual(
            BrowserRetryDisposition.DispatchedObserveOnly,
            BrowserRetryPolicy.Decide(receipt).Disposition);
    }

    [TestMethod]
    public void RetryDecision_PreservesTheReceiptsMinimalRepairHint()
    {
        // 「不可自动重试」不等于「无事可做」：引用过期时必须告诉调用方**怎么刷新**。
        // 提示由回执携带，重试裁定在兜底分支回带（否则调用方只得到一个干巴巴的 not_retryable）。
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Click,
            execution: BrowserActionExecution.NotStarted,
            error: new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch, "stale ref", retryable: false),
            detail: "re-observe the page and re-locate"));

        var decision = BrowserRetryPolicy.Decide(receipt);

        Assert.AreEqual(BrowserRetryDisposition.NotRetryable, decision.Disposition);
        Assert.AreEqual("re-observe the page and re-locate", decision.Detail);
    }

    [TestMethod]
    public void RetryDecision_DoesNotInventAHint_WhenThereIsNone()
    {
        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Click,
            execution: BrowserActionExecution.NotStarted,
            error: new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch, "moved on", retryable: false)));

        Assert.IsNull(BrowserRetryPolicy.Decide(receipt).Detail);
    }

    [TestMethod]
    public void UnknownOutcome_IsNeverTreatedAsNotExecuted()    {
        var unknown = DesktopCapabilityError.OutcomeUnknown("connection dropped");

        var receipt = BrowserInteractionReceipt.Build(Evidence(
            DesktopInteractionAction.Click,
            execution: BrowserInteractionReceipt.ExecutionFor(unknown),
            error: unknown));

        Assert.AreEqual(BrowserActionCompletion.Unknown, receipt.Completion);
        Assert.IsFalse(receipt.Ok);
        Assert.AreEqual(
            BrowserRetryDisposition.UnknownOutcome,
            BrowserRetryPolicy.Decide(receipt).Disposition);
    }

    private static BrowserActionAssertion AssertSingle(BrowserActionReceipt receipt)
    {
        Assert.AreEqual(1, receipt.Assertions.Count);
        return receipt.Assertions[0];
    }
}
