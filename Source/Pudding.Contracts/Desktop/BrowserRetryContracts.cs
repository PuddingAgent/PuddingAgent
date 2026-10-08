namespace Pudding.Contracts.Desktop;

/// <summary>
/// 重试裁定（方案 §4.3）。回执已经给出 <see cref="BrowserActionReceipt.SafeToRetry"/>，
/// 这里表达「调用方/缓存层打算重发时」的完整裁定，包括缓存过期的单独一种。
/// </summary>
public enum BrowserRetryDisposition
{
    /// <summary>不允许重试（含业务失败且已派发）。</summary>
    NotRetryable,

    /// <summary>确定未开始且故障可重试 ⇒ 允许重发。</summary>
    SafeToRetry,

    /// <summary>已派发但效果未验证 ⇒ 先观察，<b>不要</b>重做。</summary>
    DispatchedObserveOnly,

    /// <summary>结果不明（断连/崩溃）⇒ 不得当作未执行，也不得自动重发。</summary>
    UnknownOutcome,

    /// <summary>缓存中的终态已过期 ⇒ 返回 <c>receipt_expired</c>，不得当成新动作执行。</summary>
    ReceiptExpired,

    /// <summary>同 operationId 复用了不同请求摘要 ⇒ 拒绝。</summary>
    OperationConflict,

    /// <summary>在途/缓存容量耗尽 ⇒ 拒绝新写请求（在途项不因容量被淘汰）。</summary>
    CapacityExhausted,
}

/// <summary>重试裁定 ↔ 线名。</summary>
public static class BrowserRetryDispositionWire
{
    public static string NameOf(BrowserRetryDisposition disposition) => disposition switch
    {
        BrowserRetryDisposition.NotRetryable => "not_retryable",
        BrowserRetryDisposition.SafeToRetry => "safe_to_retry",
        BrowserRetryDisposition.DispatchedObserveOnly => "dispatched_observe_only",
        BrowserRetryDisposition.UnknownOutcome => "unknown_outcome",
        BrowserRetryDisposition.ReceiptExpired => "receipt_expired",
        BrowserRetryDisposition.OperationConflict => "operation_conflict",
        BrowserRetryDisposition.CapacityExhausted => "capacity_exhausted",
        _ => throw new ArgumentOutOfRangeException(
            nameof(disposition), disposition, "Retry disposition is not registered."),
    };

    public static bool TryParse(string? name, out BrowserRetryDisposition disposition)
    {
        foreach (var candidate in Enum.GetValues<BrowserRetryDisposition>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                disposition = candidate;
                return true;
            }
        }

        disposition = default;
        return false;
    }

    /// <summary>全部已登记裁定（快照断言用）。</summary>
    public static IReadOnlyList<BrowserRetryDisposition> All { get; } =
        Enum.GetValues<BrowserRetryDisposition>();
}

/// <summary>
/// 重试裁定结果：结论 + 是否可能有副作用 + 可选的领域错误 + 可选回执。
///
/// 这是<b>纯数据</b>：判定逻辑在 <c>PuddingBrowser.Automation</c> 的
/// <c>BrowserRetryPolicy</c> 与 <c>BrowserOperationLedger</c> 内，契约层不持有判断。
/// </summary>
public sealed record BrowserRetryDecision(
    BrowserRetryDisposition Disposition,
    bool MayHaveSideEffects,
    BrowserActionReceipt? Receipt = null,
    DesktopCapabilityError? Error = null,
    string? Detail = null)
{
    public bool IsSafeToRetry => Disposition == BrowserRetryDisposition.SafeToRetry;

    public string WireDisposition => BrowserRetryDispositionWire.NameOf(Disposition);

    public override string ToString() =>
        $"{WireDisposition}(sideEffects={MayHaveSideEffects},"
        + $" receipt={(Receipt is { } receipt ? BrowserActionCompletionWire.NameOf(receipt.Completion) : "-")})";
}
