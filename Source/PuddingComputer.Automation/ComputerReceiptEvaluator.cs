using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>由 <see cref="ComputerReceiptEvaluator"/> 推出的三件事：终态、有无副作用、能否安全重试。</summary>
public readonly record struct ComputerReceiptVerdict(
    ComputerCompletion Completion,
    bool MayHaveSideEffects,
    bool SafeToRetry);

/// <summary>
/// 回执判定（设计 §7.2 的六种情况表）。
/// <para>
/// 为什么不把这张表写进驱动：驱动只知道"我调用了什么"，不知道"这件事该被怎么理解"。
/// 而"能否重试"这种判断一旦分散在各调用点，就会出现某处把 <c>partial_input</c> 当成可重试 ——
/// 结果是重复副作用，且**不会有任何报错**。
/// </para>
/// </summary>
public static class ComputerReceiptEvaluator
{
    /// <param name="execution">驱动程序有没有被调用、输入有没有提交出去。</param>
    /// <param name="verification">后置条件验证结果。</param>
    /// <param name="submittedInputCount">实际提交的输入数量（如按键数），未知传 null。</param>
    /// <param name="expectedInputCount">预期提交数量，未约定传 null。</param>
    public static ComputerReceiptVerdict Evaluate(
        ComputerExecution execution,
        ComputerVerification verification,
        int? submittedInputCount = null,
        int? expectedInputCount = null)
    {
        // ① 拦在驱动之前：什么都没发生，可以安全重试（但要先刷新/恢复，不能盲用旧参数）
        if (execution == ComputerExecution.NotStarted)
            return new ComputerReceiptVerdict(ComputerCompletion.Blocked, false, true);

        // ② 终态未知：保守认为可能已生效，绝不标安全重试
        if (execution == ComputerExecution.Unknown)
            return new ComputerReceiptVerdict(ComputerCompletion.Unknown, true, false);

        // ③ 部分提交：比"验证失败"更具体，必须先说清楚只进去了一半
        if (submittedInputCount is { } submitted && expectedInputCount is { } expected && submitted < expected)
            return new ComputerReceiptVerdict(ComputerCompletion.Failed, true, false);

        // ④ 验证明确失败
        if (verification == ComputerVerification.Failed)
            return new ComputerReceiptVerdict(ComputerCompletion.Failed, true, false);

        // ⑤ 验证通过：有新证据，可以完成
        if (verification == ComputerVerification.Passed)
            return new ComputerReceiptVerdict(ComputerCompletion.Verified, true, false);

        // ⑥ 提交了但没验证（或未要求验证）：先观察，别急着认为成功
        return new ComputerReceiptVerdict(ComputerCompletion.Dispatched, true, false);
    }
}
