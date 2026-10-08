using Pudding.Contracts;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// 能力路径的失败 → 工具错误码。**只有这一张表**：每个工具各写一份必然漂移
/// （本系列已经在两条传输之间吃过这个亏）。
///
/// 版本不符的语义**按工具不同**，因此由调用方给出：定位是"引用过期"（`stale_element_reference`），
/// 快照/页状态是"页面已变"（`stale_page_version`）。这不算各写一份——判定仍集中在调用方一处。
/// </summary>
internal static class BrowserCapabilityFailure
{
    /// <summary>游标过期时唯一的正确动作是重新读取，不是重试同一个请求（§6.2）。</summary>
    internal const string CursorExpiredHint =
        "the cursor belongs to an older page version or a different locator: re-read (call again without the cursor) and page from the fresh result";

    public static ToolExecutionResult From(
        DesktopCapabilityError error,
        string fallbackCode,
        string versionMismatchCode,
        object? value = null,
        IReadOnlyList<string>? warnings = null) => BrowserToolResponse.Failure(
        error.Code switch
        {
            DesktopCapabilityErrorCode.InvalidTarget => "browser_page_not_found",
            DesktopCapabilityErrorCode.PageVersionMismatch => versionMismatchCode,
            DesktopCapabilityErrorCode.InvalidRequest => "browser_invalid_arguments",
            DesktopCapabilityErrorCode.UnsupportedCapability => "browser_unsupported",
            // 游标过期是**独立错误码**（§6.2）：必须与"参数错""没找到"区分开。
            DesktopCapabilityErrorCode.CursorExpired => "cursor_expired",
            _ => fallbackCode,
        },
        error.Message,
        value: value,
        warnings: warnings ?? (error.Code == DesktopCapabilityErrorCode.CursorExpired
            ? [CursorExpiredHint]
            : null));
}
