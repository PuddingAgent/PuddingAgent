namespace Pudding.Contracts;

/// <summary>
/// 领域错误码（计划 §5「领域错误至少包括」）。
/// 流本身故障用 gRPC status；单次业务失败用结果错误，避免一条命令失败关闭整个通道。
/// </summary>
public enum DesktopCapabilityErrorCode
{
    /// <summary>能力未声明或未协商成功。</summary>
    UnsupportedCapability,

    /// <summary>尚未握手成功，或连接已失效。</summary>
    NotConnected,

    /// <summary>连接在操作开始前断开：可确认未执行。</summary>
    Disconnected,

    /// <summary>认证或授权失败（含越权目标）。</summary>
    Unauthorized,

    /// <summary>目标不存在/非法（含旧世代命令与跨 Desktop 目标）。</summary>
    InvalidTarget,

    /// <summary>页面版本不匹配：Snapshot/Locator 已失效，必须重新获取状态。</summary>
    PageVersionMismatch,

    /// <summary>工具运行时被用户暂停。</summary>
    Paused,

    /// <summary>用户接管了浏览器，自动化被中止。</summary>
    UserTakeover,

    /// <summary>超过单操作期限。</summary>
    DeadlineExceeded,

    /// <summary>被显式取消（尽力而为，不能撤销已执行的脚本）。</summary>
    Cancelled,

    /// <summary>执行可能已完成但结果丢失（跨进程崩溃/断连）。调用者必须查询或交由用户处理。</summary>
    OutcomeUnknown,

    /// <summary>队列/在途预算耗尽。</summary>
    ResourceExhausted,

    /// <summary>UI 队列拒绝或窗口已退出。</summary>
    UiUnavailable,

    /// <summary>请求不合法（缺字段、重复 ID 不同 payload、协商越权声明等）。</summary>
    InvalidRequest,

    /// <summary>未归类的内部故障。</summary>
    InternalError,
}

/// <summary>
/// 错误码 ↔ 线名/默认语义的真源。线名冻结并由契约测试快照断言。
/// </summary>
public static class DesktopCapabilityErrorCodes
{
    private static readonly (DesktopCapabilityErrorCode Code, string Wire, bool Retryable, bool MayHaveSideEffects)[] Table =
    [
        (DesktopCapabilityErrorCode.UnsupportedCapability, "unsupported_capability", false, false),
        (DesktopCapabilityErrorCode.NotConnected, "not_connected", true, false),
        (DesktopCapabilityErrorCode.Disconnected, "disconnected", true, false),
        (DesktopCapabilityErrorCode.Unauthorized, "unauthorized", false, false),
        (DesktopCapabilityErrorCode.InvalidTarget, "invalid_target", false, false),
        (DesktopCapabilityErrorCode.PageVersionMismatch, "page_version_mismatch", true, false),
        (DesktopCapabilityErrorCode.Paused, "paused", true, false),
        (DesktopCapabilityErrorCode.UserTakeover, "user_takeover", true, false),
        // 默认按「尚未开始执行」保守取值；执行中过期/取消由调用方显式标注 mayHaveSideEffects=true。
        (DesktopCapabilityErrorCode.DeadlineExceeded, "deadline_exceeded", true, false),
        (DesktopCapabilityErrorCode.Cancelled, "cancelled", true, false),
        (DesktopCapabilityErrorCode.OutcomeUnknown, "outcome_unknown", false, true),
        (DesktopCapabilityErrorCode.ResourceExhausted, "resource_exhausted", true, false),
        (DesktopCapabilityErrorCode.UiUnavailable, "ui_unavailable", true, false),
        (DesktopCapabilityErrorCode.InvalidRequest, "invalid_request", false, false),
        (DesktopCapabilityErrorCode.InternalError, "internal_error", false, true),
    ];

    private static readonly Dictionary<DesktopCapabilityErrorCode, (string Wire, bool Retryable, bool MayHaveSideEffects)> ByCode =
        Table.ToDictionary(entry => entry.Code, entry => (entry.Wire, entry.Retryable, entry.MayHaveSideEffects));

    private static readonly Dictionary<string, DesktopCapabilityErrorCode> ByWire =
        Table.ToDictionary(entry => entry.Wire, entry => entry.Code, StringComparer.Ordinal);

    public static IReadOnlyList<DesktopCapabilityErrorCode> All { get; } = Table.Select(entry => entry.Code).ToArray();

    public static string NameOf(DesktopCapabilityErrorCode code) =>
        ByCode.TryGetValue(code, out var entry)
            ? entry.Wire
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Error code is not registered.");

    public static bool TryParse(string? wire, out DesktopCapabilityErrorCode code)
    {
        code = default;
        return !string.IsNullOrEmpty(wire) && ByWire.TryGetValue(wire, out code);
    }

    public static bool DefaultRetryable(DesktopCapabilityErrorCode code) =>
        ByCode.TryGetValue(code, out var entry) && entry.Retryable;

    public static bool DefaultMayHaveSideEffects(DesktopCapabilityErrorCode code) =>
        ByCode.TryGetValue(code, out var entry) && entry.MayHaveSideEffects;

    /// <summary>把未知线名确定性地折叠成 <see cref="DesktopCapabilityErrorCode.InternalError"/>（fail closed）。</summary>
    public static DesktopCapabilityErrorCode ParseOrInternalError(string? wire) =>
        TryParse(wire, out var code) ? code : DesktopCapabilityErrorCode.InternalError;
}

/// <summary>
/// 单次能力调用的失败结果。
///
/// 结构与审计同构：只允许「码 + 脱敏消息 + 两个语义标志」，不承载脚本正文、URL、
/// 剪贴板内容或页面数据，避免错误路径成为泄密面。
/// </summary>
public sealed record DesktopCapabilityError
{
    public const int MaxMessageLength = 512;

    public DesktopCapabilityError(
        DesktopCapabilityErrorCode code,
        string? message = null,
        bool? retryable = null,
        bool? mayHaveSideEffects = null)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Error code is not registered.");
        }

        Code = code;
        var normalized = ContractText.NormalizeDisplayText(message, MaxMessageLength);
        Message = normalized.Length == 0 ? DesktopCapabilityErrorCodes.NameOf(code) : normalized;
        Retryable = retryable ?? DesktopCapabilityErrorCodes.DefaultRetryable(code);
        MayHaveSideEffects = mayHaveSideEffects ?? DesktopCapabilityErrorCodes.DefaultMayHaveSideEffects(code);
    }

    public DesktopCapabilityErrorCode Code { get; }

    /// <summary>面向用户/日志的安全描述；已剔除控制字符并截断。不保证本地化。</summary>
    public string Message { get; }

    /// <summary>按默认语义该错误是否可安全重试（<see cref="MayHaveSideEffects"/> 为真时不可重试）。</summary>
    public bool Retryable { get; }

    /// <summary>为真表示「可能已产生副作用」，调用方不得当作「未执行」处理。</summary>
    public bool MayHaveSideEffects { get; }

    /// <summary>是否可以安全重试：可重试且无副作用。</summary>
    public bool IsSafeToRetry => Retryable && !MayHaveSideEffects;

    public string WireCode => DesktopCapabilityErrorCodes.NameOf(Code);

    public static DesktopCapabilityError UnsupportedCapability(string capability) =>
        new(DesktopCapabilityErrorCode.UnsupportedCapability, $"Capability '{capability}' is not supported.");

    public static DesktopCapabilityError NotConnected(string detail) =>
        new(DesktopCapabilityErrorCode.NotConnected, detail);

    public static DesktopCapabilityError Unauthorized(string detail) =>
        new(DesktopCapabilityErrorCode.Unauthorized, detail);

    public static DesktopCapabilityError InvalidTarget(string detail) =>
        new(DesktopCapabilityErrorCode.InvalidTarget, detail);

    public static DesktopCapabilityError InvalidRequest(string detail) =>
        new(DesktopCapabilityErrorCode.InvalidRequest, detail);

    public static DesktopCapabilityError DeadlineExceeded(bool mayHaveSideEffects) =>
        new(DesktopCapabilityErrorCode.DeadlineExceeded, null, mayHaveSideEffects: mayHaveSideEffects);

    public static DesktopCapabilityError Cancelled(bool mayHaveSideEffects) =>
        new(DesktopCapabilityErrorCode.Cancelled, null, mayHaveSideEffects: mayHaveSideEffects);

    public static DesktopCapabilityError OutcomeUnknown(string detail) =>
        new(DesktopCapabilityErrorCode.OutcomeUnknown, detail);

    public static DesktopCapabilityError ResourceExhausted(string detail) =>
        new(DesktopCapabilityErrorCode.ResourceExhausted, detail);

    public static DesktopCapabilityError UiUnavailable(string detail) =>
        new(DesktopCapabilityErrorCode.UiUnavailable, detail);

    public static DesktopCapabilityError Disconnected(string detail) =>
        new(DesktopCapabilityErrorCode.Disconnected, detail);

    public static DesktopCapabilityError Internal(string detail) =>
        new(DesktopCapabilityErrorCode.InternalError, detail);

    public override string ToString() =>
        $"{WireCode}: {Message} (retryable={Retryable}, mayHaveSideEffects={MayHaveSideEffects})";
}
