namespace Pudding.Contracts.Desktop;

/// <summary>
/// 动作的<b>执行</b>阶段（方案 §4.1）。与「业务是否成功」严格分开：
/// 派发成功不等于业务成功，超时也不等于「没执行」。
/// </summary>
public enum BrowserActionExecution
{
    /// <summary>可以确定没有开始执行（准入被拒、入队前取消）。</summary>
    NotStarted,

    /// <summary>输入/请求已提交，但驱动无法确认其后续效果（例如点击是否触发导航）。</summary>
    Dispatched,

    /// <summary>执行完成（驱动侧步骤已结束）。</summary>
    Completed,

    /// <summary>无法证明执行到哪一步（断连、崩溃）。<b>不得</b>当作未执行。</summary>
    Unknown,
}

/// <summary>动作的<b>验证</b>结果（业务后置断言）。</summary>
public enum BrowserActionVerification
{
    /// <summary>调用方没有声明业务 expect，因此没有可验证的断言。</summary>
    NotRequested,

    /// <summary>必需断言全部通过。</summary>
    Passed,

    /// <summary>存在未通过的必需断言。</summary>
    Failed,

    /// <summary>声明了断言但无法取得证据（例如节点在重渲染中消失）。</summary>
    Unverified,
}

/// <summary>
/// 对外回执的<b>完成度</b>，由执行 + 验证两个事实派生。工具层 <c>ok</c> 只依据它，
/// 不再由动作名推断（方案 §1.1：不能按动作名推断导航）。
/// </summary>
public enum BrowserActionCompletion
{
    /// <summary>执行完成且必需断言通过。</summary>
    Verified,

    /// <summary>已派发但效果未验证 —— 必须先观察，**不要重做**。</summary>
    Dispatched,

    /// <summary>被闸门拒绝（接管/暂停/无授权），动作从未开始。</summary>
    Blocked,

    /// <summary>失败；若从未派发则可能可安全重试。</summary>
    Failed,

    /// <summary>结果不明；可能已产生副作用。</summary>
    Unknown,
}

/// <summary>后置断言类型（方案 §4.2 表格的「必需/可选证据」列）。</summary>
public enum BrowserAssertionKind
{
    /// <summary>同一节点句柄回读 value / 编辑文本。</summary>
    Value,

    /// <summary>checked 状态满足期望（含原本已满足的 no-op）。</summary>
    Checked,

    /// <summary>selectedValues 满足期望。</summary>
    SelectedValues,

    /// <summary>URL 满足期望（导航接受/完成）。</summary>
    Url,

    /// <summary>指定文本出现。</summary>
    TextPresent,

    /// <summary>元素状态满足期望（可见/可用/存在）。</summary>
    ElementState,

    /// <summary>容器/页面滚动偏移满足期望。</summary>
    ScrollOffset,

    /// <summary>焦点落到期望目标。</summary>
    Focus,
}

/// <summary>断言类型 ↔ 线名（真源在本文件；快照由契约测试断言）。</summary>
public static class BrowserAssertionKindWire
{
    public static string NameOf(BrowserAssertionKind kind) => kind switch
    {
        BrowserAssertionKind.Value => "value",
        BrowserAssertionKind.Checked => "checked",
        BrowserAssertionKind.SelectedValues => "selected_values",
        BrowserAssertionKind.Url => "url",
        BrowserAssertionKind.TextPresent => "text_present",
        BrowserAssertionKind.ElementState => "element_state",
        BrowserAssertionKind.ScrollOffset => "scroll_offset",
        BrowserAssertionKind.Focus => "focus",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Assertion kind is not registered."),
    };

    public static bool TryParse(string? name, out BrowserAssertionKind kind)
    {
        foreach (var candidate in Enum.GetValues<BrowserAssertionKind>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>执行阶段 ↔ 线名（回执 JSON 的一部分）。</summary>
public static class BrowserActionExecutionWire
{
    public static string NameOf(BrowserActionExecution execution) => execution switch
    {
        BrowserActionExecution.NotStarted => "not_started",
        BrowserActionExecution.Dispatched => "dispatched",
        BrowserActionExecution.Completed => "completed",
        BrowserActionExecution.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(execution), execution, "Execution stage is not registered."),
    };

    public static bool TryParse(string? name, out BrowserActionExecution execution)
    {
        foreach (var candidate in Enum.GetValues<BrowserActionExecution>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                execution = candidate;
                return true;
            }
        }

        execution = default;
        return false;
    }
}

/// <summary>验证结果 ↔ 线名。</summary>
public static class BrowserActionVerificationWire
{
    public static string NameOf(BrowserActionVerification verification) => verification switch
    {
        BrowserActionVerification.NotRequested => "not_requested",
        BrowserActionVerification.Passed => "passed",
        BrowserActionVerification.Failed => "failed",
        BrowserActionVerification.Unverified => "unverified",
        _ => throw new ArgumentOutOfRangeException(
            nameof(verification), verification, "Verification result is not registered."),
    };

    public static bool TryParse(string? name, out BrowserActionVerification verification)
    {
        foreach (var candidate in Enum.GetValues<BrowserActionVerification>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                verification = candidate;
                return true;
            }
        }

        verification = default;
        return false;
    }
}

/// <summary>完成度 ↔ 线名。</summary>
public static class BrowserActionCompletionWire
{
    public static string NameOf(BrowserActionCompletion completion) => completion switch
    {
        BrowserActionCompletion.Verified => "verified",
        BrowserActionCompletion.Dispatched => "dispatched",
        BrowserActionCompletion.Blocked => "blocked",
        BrowserActionCompletion.Failed => "failed",
        BrowserActionCompletion.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(
            nameof(completion), completion, "Completion is not registered."),
    };

    public static bool TryParse(string? name, out BrowserActionCompletion completion)
    {
        foreach (var candidate in Enum.GetValues<BrowserActionCompletion>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                completion = candidate;
                return true;
            }
        }

        completion = default;
        return false;
    }
}

/// <summary>
/// 一条后置断言证据。
///
/// 脱敏规则（方案 §4.1）：回执里的实际值<b>必须有长度上限</b>，敏感字段（密码等）
/// <b>只允许</b>用 <c>matched + length</c> 表达，绝不回显原文；日志只用
/// <see cref="ToAuditSummary"/>（类型 + 是否通过，不含值）。
/// </summary>
public sealed record BrowserActionAssertion
{
    /// <summary>非敏感实际值的展示上限（超出即截断并标注 <see cref="IsTruncated"/>）。</summary>
    public const int MaxActualLength = 160;

    public BrowserActionAssertion(
        BrowserAssertionKind kind,
        bool matched,
        string? actual = null,
        bool sensitive = false)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Assertion kind is not registered.");
        }

        Kind = kind;
        Matched = matched;
        IsSensitive = sensitive;

        if (actual is null)
        {
            RawLength = null;
            Actual = null;
            return;
        }

        RawLength = actual.Length;
        // 敏感字段：只保留长度，不留任何原文（连截断后的前缀也不留）。
        Actual = sensitive ? null : ContractText.NormalizeDisplayText(actual, MaxActualLength);
    }

    public BrowserAssertionKind Kind { get; }

    /// <summary>断言是否成立。</summary>
    public bool Matched { get; }

    /// <summary>非敏感时的（已截断）实际值；敏感时恒为 <c>null</c>。</summary>
    public string? Actual { get; }

    /// <summary>实际值的原始长度：敏感字段用它表达「填了内容」而不泄露内容。</summary>
    public int? RawLength { get; }

    public bool IsSensitive { get; }

    /// <summary>非敏感值是否因超出上限被截断（截断必须显式标注，不静默）。</summary>
    public bool IsTruncated => RawLength is { } length && length > MaxActualLength;

    /// <summary>审计/日志摘要：只有类型与是否通过，<b>不含任何值</b>。</summary>
    public string ToAuditSummary() =>
        $"{BrowserAssertionKindWire.NameOf(Kind)}={(Matched ? "matched" : "not_matched")}"
        + (IsSensitive ? "(sensitive)" : string.Empty);

    public override string ToString() => ToAuditSummary();
}

/// <summary>观察戳：某个时刻观察到的页面版本。</summary>
public sealed record BrowserObservationStamp
{
    public BrowserObservationStamp(DesktopPageVersion pageVersion, DateTimeOffset observedAtUtc)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Observation time must be UTC.", nameof(observedAtUtc));
        }

        PageVersion = pageVersion;
        ObservedAtUtc = observedAtUtc;
    }

    public DesktopPageVersion PageVersion { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public override string ToString() => $"v{PageVersion.Value}@{ObservedAtUtc:O}";
}

/// <summary>
/// 导航 / 滚动 / 焦点摘要（方案 §4.1）。只记事实与偏移，不含页面内容。
/// </summary>
public sealed record BrowserActionEffectSummary
{
    public static readonly BrowserActionEffectSummary None = new();

    public Uri? UrlBefore { get; init; }

    public Uri? UrlAfter { get; init; }

    public double? ScrollOffsetBefore { get; init; }

    public double? ScrollOffsetAfter { get; init; }

    /// <summary>滚动已到边界（此时请求更多位移是 no-op，不是失败）。</summary>
    public bool ScrollAtBoundary { get; init; }

    /// <summary>焦点落到的元素 ref（opaque 值，不含页面文本）。</summary>
    public string? FocusedElementRef { get; init; }

    /// <summary>
    /// 是否观测到导航（两次 URL 都存在且不同）。
    /// 按 <see cref="Uri.AbsoluteUri"/> 逐字比较：<see cref="Uri.Equals(Uri)"/> 会忽略 fragment，
    /// 于是「只变 hash 的 SPA 路由跳转」会被误判成没有导航。
    /// </summary>
    public bool NavigationObserved =>
        UrlBefore is not null
        && UrlAfter is not null
        && !string.Equals(UrlBefore.AbsoluteUri, UrlAfter.AbsoluteUri, StringComparison.Ordinal);

    /// <summary>实际滚动位移；缺任一端时为 null（不编造）。</summary>
    public double? ScrollDelta =>
        ScrollOffsetBefore is { } before && ScrollOffsetAfter is { } after ? after - before : null;

    /// <summary>是否真的发生了位移（零位移不得假称滚动成功）。</summary>
    public bool ScrollMoved => ScrollDelta is { } delta && delta != 0;

    public override string ToString() =>
        $"url {UrlBefore} → {UrlAfter}; scroll {ScrollOffsetBefore} → {ScrollOffsetAfter}"
        + (ScrollAtBoundary ? " (boundary)" : string.Empty)
        + (FocusedElementRef is null ? string.Empty : $"; focus {FocusedElementRef}");
}

/// <summary>
/// 动作回执（方案 §4.1）。<b>不可自相矛盾</b>：<see cref="Completion"/>、<see cref="Ok"/>、
/// <see cref="SafeToRetry"/> 全部由构造入参派生，调用方无法直接写死一个「假成功」。
/// </summary>
public sealed record BrowserActionReceipt
{
    public BrowserActionReceipt(
        OperationId operationId,
        BrowserAutomationOperation operation,
        BrowserActionExecution execution,
        BrowserActionVerification verification,
        DesktopPageTarget? target = null,
        IReadOnlyList<BrowserActionAssertion>? assertions = null,
        BrowserObservationStamp? before = null,
        BrowserObservationStamp? after = null,
        BrowserActionEffectSummary? effects = null,
        bool mayHaveSideEffects = false,
        bool retryable = false,
        bool isBlocked = false,
        DesktopCapabilityError? error = null,
        string? detail = null)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Automation operation is not registered.");
        }

        if (!Enum.IsDefined(execution))
        {
            throw new ArgumentOutOfRangeException(nameof(execution), execution, "Execution stage is not registered.");
        }

        if (!Enum.IsDefined(verification))
        {
            throw new ArgumentOutOfRangeException(nameof(verification), verification, "Verification result is not registered.");
        }

        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        Operation = operation;
        Execution = execution;
        Verification = verification;
        Target = target;
        Assertions = assertions ?? [];
        Before = before;
        After = after;
        Effects = effects ?? BrowserActionEffectSummary.None;
        MayHaveSideEffects = mayHaveSideEffects;
        Retryable = retryable;
        IsBlocked = isBlocked;
        Error = error;
        Detail = detail is null ? null : ContractText.NormalizeDisplayText(detail, 512);
    }

    public OperationId OperationId { get; }

    public BrowserAutomationOperation Operation { get; }

    public DesktopPageTarget? Target { get; }

    public BrowserActionExecution Execution { get; }

    public BrowserActionVerification Verification { get; }

    public IReadOnlyList<BrowserActionAssertion> Assertions { get; }

    public BrowserObservationStamp? Before { get; }

    public BrowserObservationStamp? After { get; }

    public BrowserActionEffectSummary Effects { get; }

    /// <summary>为真表示「可能已产生副作用」，调用方不得当作未执行。</summary>
    public bool MayHaveSideEffects { get; }

    /// <summary>故障本身是否属于可重试类别（与是否已经派发无关）。</summary>
    public bool Retryable { get; }

    /// <summary>被闸门拒绝（而非执行失败）。</summary>
    public bool IsBlocked { get; }

    /// <summary>拒绝/失败时的领域错误；没有则为 null。</summary>
    public DesktopCapabilityError? Error { get; }

    /// <summary>面向调用方的安全描述（已剔除控制字符并截断）。</summary>
    public string? Detail { get; }

    /// <summary>
    /// 完成度。<c>verified</c> 的前提是<b>执行完成且必需断言通过</b>；
    /// <c>dispatched</c> 覆盖「已提交但无业务 expect / 无法确认效果」。
    /// </summary>
    public BrowserActionCompletion Completion => Execution switch
    {
        BrowserActionExecution.Unknown => BrowserActionCompletion.Unknown,
        BrowserActionExecution.NotStarted => IsBlocked
            ? BrowserActionCompletion.Blocked
            : BrowserActionCompletion.Failed,
        BrowserActionExecution.Dispatched => Verification == BrowserActionVerification.Failed
            ? BrowserActionCompletion.Failed
            : BrowserActionCompletion.Dispatched,
        BrowserActionExecution.Completed => Verification switch
        {
            BrowserActionVerification.Passed => BrowserActionCompletion.Verified,
            BrowserActionVerification.Failed => BrowserActionCompletion.Failed,
            // 执行完成但没有可验证的业务后置条件：按「已派发未验证」如实汇报，不升级成 verified。
            _ => BrowserActionCompletion.Dispatched,
        },
        _ => BrowserActionCompletion.Unknown,
    };

    /// <summary>
    /// 工具层 <c>ok</c>：只对 verified 或明确标注的 dispatched 为真；
    /// unknown/blocked/failed 一律为假（但不丢失已派发的事实，见 <see cref="Execution"/>）。
    /// </summary>
    public bool Ok => Completion is BrowserActionCompletion.Verified or BrowserActionCompletion.Dispatched;

    public bool IsVerified => Completion == BrowserActionCompletion.Verified;

    /// <summary>只有「确定未开始 + 故障可重试 + 无副作用」才允许自动重试（方案 §4.3）。</summary>
    public bool SafeToRetry =>
        Execution == BrowserActionExecution.NotStarted && Retryable && !MayHaveSideEffects;

    /// <summary>需要调用方先观察（勿重做）：已派发未验证或结果不明。</summary>
    public bool RequiresFollowUp =>
        Completion is BrowserActionCompletion.Dispatched or BrowserActionCompletion.Unknown;

    /// <summary>失败/未通过的必要断言（供工具层给出最小修复提示）。</summary>
    public IReadOnlyList<BrowserActionAssertion> FailedAssertions =>
        Assertions.Where(assertion => !assertion.Matched).ToArray();

    public override string ToString()
    {
        var completion = BrowserActionCompletionWire.NameOf(Completion);
        var execution = BrowserActionExecutionWire.NameOf(Execution);
        var verification = BrowserActionVerificationWire.NameOf(Verification);

        return $"receipt {OperationId.Value} {Operation} {completion}"
            + $" (execution={execution}, verification={verification},"
            + $" sideEffects={MayHaveSideEffects}, safeToRetry={SafeToRetry})";
    }
}
