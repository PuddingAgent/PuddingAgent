using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Automation;

/// <summary>
/// 驱动侧观察到的原始证据。判定（不是搬运）发生在 <see cref="BrowserReceiptJudge"/>：
/// 调用方只报告「执行到哪一步」与「观测到什么」，不自己写 completion 或 ok。
/// </summary>
public sealed record BrowserActionEvidence(
    BrowserAutomationOperation Operation,
    OperationId OperationId,
    BrowserActionExecution Execution,
    IReadOnlyList<BrowserActionAssertion>? Assertions = null,
    bool RequiresAssertions = false,
    bool IsBlocked = false,
    bool UnableToVerify = false,
    DesktopPageTarget? Target = null,
    BrowserObservationStamp? Before = null,
    BrowserObservationStamp? After = null,
    BrowserActionEffectSummary? Effects = null,
    DesktopCapabilityError? Error = null,
    string? Detail = null);

/// <summary>
/// 回执判定（设计方案 §4.1/§4.2）：把「执行阶段 + 后置断言」判成
/// <see cref="BrowserActionVerification"/>，再由回执类型派生 completion/ok/safeToRetry。
///
/// 关键取舍：<b>不按动作名推断导航</b>。click/Enter 也可能只打开菜单或触发 SPA 更新，
/// 因此这里只看调用方声明的 expect 与驱动观测到的事实。
/// </summary>
public static class BrowserReceiptJudge
{
    /// <summary>按证据判定回执。所有派生字段（completion/ok/safeToRetry）由回执类型自身保证一致。</summary>
    public static BrowserActionReceipt Judge(BrowserActionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var verification = JudgeVerification(evidence);
        var sideEffecting = BrowserOperationPolicy.IsSideEffecting(evidence.Operation);
        var assertions = evidence.Assertions ?? Array.Empty<BrowserActionAssertion>();

        // 「可能已产生副作用」= 驱动这么说，或副作用操作确实进入了派发之后的阶段。
        var mayHaveSideEffects =
            evidence.Error?.MayHaveSideEffects == true
            || (sideEffecting
                && evidence.Execution is BrowserActionExecution.Dispatched
                    or BrowserActionExecution.Completed
                    or BrowserActionExecution.Unknown);

        return new BrowserActionReceipt(
            evidence.OperationId,
            evidence.Operation,
            evidence.Execution,
            verification,
            evidence.Target,
            assertions,
            evidence.Before,
            evidence.After,
            evidence.Effects,
            mayHaveSideEffects,
            evidence.Error?.Retryable == true,
            evidence.IsBlocked,
            evidence.Error,
            evidence.Detail);
    }

    /// <summary>
    /// 验证结果判定：
    /// • 未执行／结果不明 ⇒ <c>not_requested</c>（没有可判定的业务事实，不得谎称通过）；
    /// • 需要断言但拿不到 ⇒ <c>unverified</c>（不是 failed：没有证据说它错了）；
    /// • 有未通过的必要断言 ⇒ <c>failed</c>。
    /// </summary>
    public static BrowserActionVerification JudgeVerification(BrowserActionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.Execution is BrowserActionExecution.NotStarted or BrowserActionExecution.Unknown)
        {
            return BrowserActionVerification.NotRequested;
        }

        if (evidence.UnableToVerify)
        {
            return BrowserActionVerification.Unverified;
        }

        var assertions = evidence.Assertions;
        if (assertions is null || assertions.Count == 0)
        {
            return evidence.RequiresAssertions
                ? BrowserActionVerification.Unverified
                : BrowserActionVerification.NotRequested;
        }

        foreach (var assertion in assertions)
        {
            if (!assertion.Matched)
            {
                return BrowserActionVerification.Failed;
            }
        }

        return BrowserActionVerification.Passed;
    }
}

/// <summary>
/// 重试裁定（设计方案 §4.3）：只有「明确未开始 + 故障可重试 + 无副作用」才是
/// <see cref="BrowserRetryDisposition.SafeToRetry"/>；已派发一律先观察，绝不重做。
/// </summary>
public static class BrowserRetryPolicy
{
    public static BrowserRetryDecision Decide(BrowserActionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var disposition = receipt.Completion switch
        {
            BrowserActionCompletion.Unknown => BrowserRetryDisposition.UnknownOutcome,
            BrowserActionCompletion.Verified => BrowserRetryDisposition.NotRetryable,
            // 被闸门拒绝时**不自动重试**：接管/暂停是用户意图，必须由调用方在恢复后显式重新准入，
            // 否则自动重试就成了绕过接管的旁路（方案 §3.1）。
            BrowserActionCompletion.Blocked => BrowserRetryDisposition.NotRetryable,
            // 动作**确实跑过**（dispatched/completed）却没通过验证 ⇒ 先观察、勿重做：
            // 「断言没过」不代表动作没发生（例如 fill 之后页面重渲染把值改回去了）。
            // 漏掉这一条会让调用方得到"不可重试"却没有任何提示——而它其实需要先去看一眼页面。
            // 注意：这里只看执行阶段，不看 mayHaveSideEffects —— 后者与 NotStarted 同时出现是
            // 自相矛盾的回执（无法判断到底跑没跑），保守地按不可重试处理，不谎称"已派发"。
            _ when receipt.Execution is BrowserActionExecution.Dispatched or BrowserActionExecution.Completed
                => BrowserRetryDisposition.DispatchedObserveOnly,
            BrowserActionCompletion.Dispatched => BrowserRetryDisposition.DispatchedObserveOnly,
            _ => receipt.SafeToRetry ? BrowserRetryDisposition.SafeToRetry : BrowserRetryDisposition.NotRetryable,
        };

        var detail = disposition switch
        {
            BrowserRetryDisposition.NotRetryable when receipt.Completion == BrowserActionCompletion.Blocked =>
                "blocked by a control/authorization gate; re-admit explicitly after the gate clears",
            BrowserRetryDisposition.DispatchedObserveOnly =>
                "the action was dispatched but its effect is unverified; observe before retrying",
            BrowserRetryDisposition.UnknownOutcome =>
                "the outcome is unknown (disconnect/crash); do not treat it as not executed",
            // 其余情况沿用回执自带的**最小修复提示**（例如"引用已过期：重新观察后再定位"）。
            // 没有提示时保持 null，不编造一句空话。
            _ => receipt.Detail,
        };

        return new BrowserRetryDecision(disposition, receipt.MayHaveSideEffects, receipt, receipt.Error, detail);
    }
}
