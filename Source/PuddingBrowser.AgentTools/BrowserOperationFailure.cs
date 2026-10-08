using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// 驱动/端口抛出的 <see cref="PuddingBrowser.Abstractions.BrowserOperationException"/> → 失败回执。
///
/// 为什么单列一张表：设计 §4.1 要求「失败不能丢失已经派发的事实」，而异常路径原先只给
/// code/message —— 调用方看不出"到底派发没有"。异常是**驱动在触碰页面之前**或**同一不可分步骤内**
/// 抛出的，因此默认判为"未开始"；只有显式标注才升级。这里同时给出**最小修复提示**
/// （§5：真正过期返回 stale_element_reference 与最小刷新提示，不自动点击"看起来相同"的节点）。
/// </summary>
internal static class BrowserOperationFailure
{
    /// <summary>把异常码映射成领域错误 + 是否可重试 + 最小修复提示。</summary>
    internal static (DesktopCapabilityError Error, string? Hint) Map(string code, string message) => code switch
    {
        // 引用已过期：可以重试**同一次调用**毫无意义（同一个 ref 一定再次失败），
        // 因此标注不可重试，并给出"重新观察再定位"的最小刷新提示。
        "stale_element_reference" => (
            new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch, message, retryable: false),
            "the element reference is stale: re-observe the page (snapshot/locate) and re-locate; never re-bind automatically"),

        "page_version_mismatch" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.PageVersionMismatch, message, retryable: true),
            "the page moved on: read the current page state, then retry"),

        "browser_invalid_arguments" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.InvalidRequest, message, retryable: false),
            "fix the arguments; nothing was dispatched"),

        "browser_operation_not_supported" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.UnsupportedCapability, message, retryable: false),
            null),

        // ── §6.2 的"动作被拒绝"三态：都没派发，但**修复方式不同**，因此提示必须不同。
        // 歧义：绝不静默选第一个，也绝不重试（同样的定位一定再次歧义）。
        "browser_locator_ambiguous" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.InvalidRequest, message, retryable: false),
            "the locator matched several elements: disambiguate with nth or a more specific locator"),

        // 零命中不等于"定位方式全坏"：页面可能还在渲染 ⇒ 可以等/重试。
        "browser_element_not_found" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.InvalidTarget, message, retryable: true),
            "no element matched: wait for it (browser_wait_for) or re-observe the page and re-locate"),

        // 不可见的写目标不算成功：§6.2 要求写操作先滚入可见区域并复检。
        "browser_element_not_visible" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.InvalidTarget, message, retryable: false),
            "the matched element is not visible: scroll it into view or choose a different target"),

        "browser_element_disabled" => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.InvalidRequest, message, retryable: false),
            "the matched element is disabled: wait until it becomes enabled"),

        // 未登记的异常码：保守地当作**可能已产生副作用**（内部错误），不得当作"没执行"。
        _ => (
            new DesktopCapabilityError(DesktopCapabilityErrorCode.InternalError, message),
            null),
    };

    /// <summary>把（已映射的）错误判成回执，并放进失败载荷的 <c>value</c>。</summary>
    internal static object Receipt(
        DesktopCapabilityError error,
        string? hint,
        DesktopInteractionAction action,
        string? expectedValue = null)
    {
        var receipt = BrowserInteractionReceipt.Build(new BrowserInteractionEvidence(
            new OperationId(Guid.NewGuid().ToString("N")),
            action,
            ExpectedValue: expectedValue,
            Execution: BrowserInteractionReceipt.ExecutionFor(error),
            Error: error,
            Detail: hint));
        var retry = BrowserRetryPolicy.Decide(receipt);

        return new BrowserReceiptToolValue
        {
            Operation = receipt.Operation.ToString(),
            Completion = BrowserActionCompletionWire.NameOf(receipt.Completion),
            Execution = BrowserActionExecutionWire.NameOf(receipt.Execution),
            Verification = BrowserActionVerificationWire.NameOf(receipt.Verification),
            MayHaveSideEffects = receipt.MayHaveSideEffects,
            SafeToRetry = receipt.SafeToRetry,
            RequiresFollowUp = receipt.RequiresFollowUp,
            RetryDisposition = BrowserRetryDispositionWire.NameOf(retry.Disposition),
            RetryDetail = retry.Detail,
        };
    }
}
