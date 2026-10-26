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

    /// <summary>
    /// 页面未授权时的可执行提示。
    ///
    /// 授权模型是**单目标页**（用户显式选中的那一页，见 AgentTargetSession）：
    /// 新建或切到另一页**不会**自动取得授权。不给这条提示，调用方只能看到一句
    /// `no page grant covers …`，既不知为何，也不知下一步该做什么。
    /// </summary>
    internal const string PageNotAuthorizedHint =
        "page_not_authorized: only one page can be driven at a time and it must be the page the user handed to this session; ask the user to hand this page over in Desktop, or switch back to the already-authorized page";

    public static ToolExecutionResult From(
        DesktopCapabilityError error,
        string fallbackCode,
        string versionMismatchCode,
        object? value = null,
        IReadOnlyList<string>? warnings = null) => BrowserToolResponse.Failure(
        error.Code switch
        {
            // 未授权是**独立错误码**：既不是“参数错”也不是“操作失败”，而是“这一页没交给你”。
            // 归到 fallback 会把授权拒绝伪装成执行失败，调用方只会反复重试同一个动作
            // （实测：browser_evaluate / browser_navigate 都报 browser_*_failed）。
            DesktopCapabilityErrorCode.Unauthorized => "browser_page_not_authorized",
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
        warnings: warnings ?? HintFor(error.Code));

    /// <summary>失败也该带“下一步怎么做”：提示按错误码固定，不随工具变。</summary>
    private static IReadOnlyList<string>? HintFor(DesktopCapabilityErrorCode code) => code switch
    {
        DesktopCapabilityErrorCode.CursorExpired => [CursorExpiredHint],
        DesktopCapabilityErrorCode.Unauthorized => [PageNotAuthorizedHint],
        _ => null,
    };
}
