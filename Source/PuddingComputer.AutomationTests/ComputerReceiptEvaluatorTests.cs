using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.AutomationTests;

/// <summary>
/// 回执判定（设计 §7.2 的六种情况表）。
/// <para>
/// 这里最容易出事的不是"算错终态"，而是**把不该重试的标成可重试**：
/// 重复副作用不会报错，只会让目标状态被改两次。
/// 所以每组用例都同时断言 SafeToRetry 与 MayHaveSideEffects。
/// </para>
/// </summary>
[TestClass]
public sealed class ComputerReceiptEvaluatorTests
{
    [TestMethod]
    public void Driver_never_called_is_blocked_and_safe_to_retry()
    {
        var v = ComputerReceiptEvaluator.Evaluate(
            ComputerExecution.NotStarted, ComputerVerification.NotRequested);

        Assert.AreEqual(ComputerCompletion.Blocked, v.Completion);
        Assert.IsFalse(v.MayHaveSideEffects, "驱动都没被调用，不可能有副作用");
        Assert.IsTrue(v.SafeToRetry, "只有拦在驱动之前这一种情况才允许直接重试");
    }

    [TestMethod]
    public void Unknown_outcome_is_never_safe_to_retry()
    {
        var v = ComputerReceiptEvaluator.Evaluate(
            ComputerExecution.Unknown, ComputerVerification.Unverified);

        Assert.AreEqual(ComputerCompletion.Unknown, v.Completion);
        Assert.IsTrue(v.MayHaveSideEffects, "断连/崩溃时必须保守认为已经生效");
        Assert.IsFalse(v.SafeToRetry, "unknown 不能当「相同参数重试」");
    }

    [TestMethod]
    public void Partial_input_is_failed_not_dispatched()
    {
        var v = ComputerReceiptEvaluator.Evaluate(
            ComputerExecution.Dispatched, ComputerVerification.NotRequested,
            submittedInputCount: 3, expectedInputCount: 7);

        Assert.AreEqual(ComputerCompletion.Failed, v.Completion,
            "只提交了 3/7，必须报失败而不是「已提交」");
        Assert.IsTrue(v.MayHaveSideEffects);
        Assert.IsFalse(v.SafeToRetry, "重发整段会让前 3 个再执行一次");
    }

    [TestMethod]
    public void Verification_failure_is_failed_and_not_retryable()
    {
        var v = ComputerReceiptEvaluator.Evaluate(
            ComputerExecution.Completed, ComputerVerification.Failed);

        Assert.AreEqual(ComputerCompletion.Failed, v.Completion);
        Assert.IsFalse(v.SafeToRetry);
    }

    [TestMethod]
    public void Verification_pass_is_verified()
    {
        var v = ComputerReceiptEvaluator.Evaluate(
            ComputerExecution.Completed, ComputerVerification.Passed);

        Assert.AreEqual(ComputerCompletion.Verified, v.Completion);
        Assert.IsFalse(v.SafeToRetry, "已经完成的事再重试就是重复副作用");
    }

    [TestMethod]
    public void Dispatched_without_expectation_stays_dispatched()
    {
        var v = ComputerReceiptEvaluator.Evaluate(
            ComputerExecution.Dispatched, ComputerVerification.NotRequested);

        Assert.AreEqual(ComputerCompletion.Dispatched, v.Completion,
            "提交了但没验证，只能说「已提交」，不能说成功");
        Assert.IsTrue(v.MayHaveSideEffects);
        Assert.IsFalse(v.SafeToRetry);
    }

    [TestMethod]
    public void Only_blocked_is_ever_safe_to_retry()
    {
        // 穷举所有 execution × verification 组合，安全重试只允许出现在 NotStarted 上
        foreach (var execution in Enum.GetValues<ComputerExecution>())
        foreach (var verification in Enum.GetValues<ComputerVerification>())
        {
            var v = ComputerReceiptEvaluator.Evaluate(execution, verification);
            if (v.SafeToRetry)
                Assert.AreEqual(ComputerExecution.NotStarted, execution,
                    $"{execution}/{verification} 被误判为可安全重试");
        }
    }

    [TestMethod]
    public void Not_started_never_reports_side_effects()
    {
        foreach (var verification in Enum.GetValues<ComputerVerification>())
        {
            var v = ComputerReceiptEvaluator.Evaluate(ComputerExecution.NotStarted, verification);
            Assert.IsFalse(v.MayHaveSideEffects,
                $"NotStarted + {verification} 不应有副作用");
        }
    }
}
