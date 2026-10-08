using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Automation;

/// <summary>
/// 一次交互的**证据**：调用方声明了什么、驱动回读了什么、执行到哪一步。
/// 判定本身在 <see cref="BrowserInteractionReceipt.Build"/>，调用方不得自己写 completion/ok。
/// </summary>
public sealed record BrowserInteractionEvidence(
    OperationId OperationId,
    DesktopInteractionAction Action,
    string? RequestedText = null,
    IReadOnlyList<string>? RequestedValues = null,
    bool? RequestedChecked = null,
    string? ExpectedValue = null,
    bool ElementObserved = false,
    string? ObservedValue = null,
    IReadOnlyList<string>? ObservedSelectedValues = null,
    bool? ObservedChecked = null,
    BrowserActionExecution Execution = BrowserActionExecution.Completed,
    DesktopPageTarget? Target = null,
    BrowserObservationStamp? Before = null,
    BrowserObservationStamp? After = null,
    DesktopCapabilityError? Error = null,
    bool IsBlocked = false,
    string? Detail = null);

/// <summary>
/// 交互回执的**判定**（设计方案 §4.1/§4.2、门禁 G7）：
/// 把「调用方声明的期望 + 驱动回读的观测值」判成断言与完成度，产出
/// <see cref="BrowserActionReceipt"/>。
///
/// 三条规则：
/// ① **不按动作名推断业务成功**：只有声明了期望（显式 <c>expect_value</c>，或
///    select 的 <c>values</c>、check/uncheck 的 <c>checked</c> 这类"调用方自己给的目标值"）
///    才有断言；没有期望就如实报 <c>dispatched</c>，不假装 verified；
/// ② **表单值一律不回显**：value/selectedValues 只给 <c>matched</c> 与长度，
///    因此「一次 fill 就能确认写入成功」不会把输入内容带进工具回执或日志；
/// ③ 期望存在但拿不到观测（元素消失/重渲染）报 <c>unverified</c>，不是 failed。
/// </summary>
public static class BrowserInteractionReceipt
{
    public static BrowserActionReceipt Build(BrowserInteractionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!Enum.IsDefined(evidence.Action))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence), evidence.Action, "Interaction action is not registered.");
        }

        var assertions = BuildAssertions(evidence, out var expectationDeclared);
        var unableToVerify = expectationDeclared && !evidence.ElementObserved;

        // 声明了期望却拿不到观测（元素消失/重渲染）：**不是** failed —— 没有证据说它错了。
        // 清空断言并置 UnableToVerify，让判定器判为 unverified。
        if (unableToVerify)
        {
            assertions = [];
        }

        return BrowserReceiptJudge.Judge(new BrowserActionEvidence(
            BrowserAutomationOperation.Interact,
            evidence.OperationId,
            evidence.Execution,
            assertions,
            RequiresAssertions: expectationDeclared,
            // 「被闸门拒绝」与「失败」必须分开：前者要求调用方在闸门解除后**显式重新准入**，
            // 后者才是可以按重试裁定处理的失败（设计方案 §3.2/§4.3）。
            IsBlocked: evidence.IsBlocked || IsGateDenial(evidence.Error),
            UnableToVerify: unableToVerify,
            Target: evidence.Target,
            Before: evidence.Before,
            After: evidence.After,
            Effects: BrowserActionEffectSummary.None,
            Error: evidence.Error,
            // 最小修复提示（例如「引用已过期：重新观察页面再定位」）随回执保留，
            // 由重试裁定在"不可自动重试"时回带给调用方 —— 否则调用方只知道失败，不知道该做什么。
            Detail: evidence.Detail));
    }

    /// <summary>
    /// 该错误是不是**闸门**拒绝（接管/暂停/授权）。这三个码语义一致：
    /// 动作**未开始**、没有任何副作用，且**不得自动重试**（接管与暂停是用户意图，
    /// 自动重试等于绕过它；授权要等重新准入）。其他失败（目标不存在、版本不符、参数错、
    /// 连接不可用…）归 <c>failed</c>，按重试裁定处理。
    /// </summary>
    public static bool IsGateDenial(DesktopCapabilityError? error) =>
        error?.Code is DesktopCapabilityErrorCode.UserTakeover
            or DesktopCapabilityErrorCode.Paused
            or DesktopCapabilityErrorCode.Unauthorized;

    /// <summary>执行阶段：由驱动/传输的错误语义推导，不需要额外知识（见错误目录的 retryable/sideEffects）。</summary>
    public static BrowserActionExecution ExecutionFor(DesktopCapabilityError? error)
    {
        if (error is null)
        {
            return BrowserActionExecution.Completed;
        }

        // 明确「未开始」的失败（准入拒绝、版本不符、目标不存在、参数错）：
        if (!error.MayHaveSideEffects)
        {
            return BrowserActionExecution.NotStarted;
        }

        // 可能已经派发（期限/断连/取消/内部错误）：如实标注，不得当作未执行。
        return error.Code == DesktopCapabilityErrorCode.OutcomeUnknown
            ? BrowserActionExecution.Unknown
            : BrowserActionExecution.Dispatched;
    }

    private static IReadOnlyList<BrowserActionAssertion> BuildAssertions(
        BrowserInteractionEvidence evidence,
        out bool expectationDeclared)
    {
        var assertions = new List<BrowserActionAssertion>(1);

        // ① 显式期望优先：调用方声明了「写完应该是这个值」。
        if (evidence.ExpectedValue is not null)
        {
            expectationDeclared = true;
            assertions.Add(ValueAssertion(evidence.ObservedValue, evidence.ExpectedValue));
            return assertions;
        }

        // ② select：调用方给的 values 就是期望（自身声明的目标状态，不是按动作名推断）。
        if (evidence.Action == DesktopInteractionAction.Select
            && evidence.RequestedValues is { Count: > 0 } requested)
        {
            expectationDeclared = true;
            assertions.Add(new BrowserActionAssertion(
                BrowserAssertionKind.SelectedValues,
                matched: SameValueSet(evidence.ObservedSelectedValues, requested),
                actual: DescribeSelected(evidence.ObservedSelectedValues),
                // 表单值一律不回显：selectedValues 只留长度。
                sensitive: true));
            return assertions;
        }

        // ③ check/uncheck：期望就是调用方声明的勾选态（布尔，非敏感）。
        if ((evidence.Action is DesktopInteractionAction.Check or DesktopInteractionAction.Uncheck)
            && evidence.RequestedChecked is bool expectedChecked)
        {
            expectationDeclared = true;
            assertions.Add(new BrowserActionAssertion(
                BrowserAssertionKind.Checked,
                matched: evidence.ObservedChecked == expectedChecked,
                actual: evidence.ObservedChecked?.ToString(),
                sensitive: false));
            return assertions;
        }

        // ④ 没有期望：不判失败，如实报 dispatched（click/press/hover/scroll 与不带 expect 的 fill/type）。
        expectationDeclared = false;
        return assertions;
    }

    /// <summary>表单值断言：只保留 matched 与长度，绝不回显内容。</summary>
    private static BrowserActionAssertion ValueAssertion(string? observed, string expected) =>
        new(
            BrowserAssertionKind.Value,
            matched: string.Equals(observed, expected, StringComparison.Ordinal),
            actual: observed,
            sensitive: true);

    private static bool SameValueSet(IReadOnlyList<string>? observed, IReadOnlyList<string> expected)
    {
        if (observed is null)
        {
            return false;
        }

        return observed.Count == expected.Count
            && !observed.Except(expected, StringComparer.Ordinal).Any()
            && !expected.Except(observed, StringComparer.Ordinal).Any();
    }

    private static string? DescribeSelected(IReadOnlyList<string>? values) =>
        values is null ? null : string.Join(",", values);
}
