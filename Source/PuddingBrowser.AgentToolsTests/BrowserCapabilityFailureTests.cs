using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// 能力失败 → 工具错误码的**唯一映射表**回归。
///
/// 背景（2026-10-26 实测）：`browser_tabs new` 建出第二页后再 evaluate/navigate，
/// Desktop 返回 `Unauthorized`（wire=unauthorized，"no page grant covers …"），
/// 但映射表没有这一分支 ⇒ 落到各工具的 fallback（`browser_evaluate_failed` / `browser_tabs_failed`），
/// **把"这一页没交给你"伪装成了"操作失败"**，调用方只会反复重试同一个动作。
///
/// 授权模型本身是单目标页（AgentTargetSession），本测试**不涉及**授权逻辑，
/// 只钉住"未授权必须作为独立错误码如实透出，并带可执行提示"。
/// </summary>
public sealed class BrowserCapabilityFailureTests
{
    [Fact]
    public void Unauthorized_IsNotDowngradedToActionFailure()
    {
        var error = DesktopCapabilityError.Unauthorized(
            "no page grant covers 'ctx/page' for this caller");

        var result = BrowserCapabilityFailure.From(error, "browser_tabs_failed", "stale_page_version");

        Assert.False(result.Success);

        // 失败载荷落在 Error（Ok 路径才是 Output）：两边拼起来查，不假设存放位置。
        var payload = string.Concat(result.Output, " | ", result.Error);

        // 独立错误码：不得再落到调用方给的 fallback。
        Assert.Contains("browser_page_not_authorized", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("browser_tabs_failed", payload, StringComparison.Ordinal);

        // 且必须带"下一步怎么做"，否则调用方只知道被拒、不知道该怎么办。
        Assert.Contains("page_not_authorized", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// 负向对照：提示**只**跟着该有的错误码出现。
    /// 没有这条，上面那个断言可能因为"所有失败都带提示"而假通过。
    /// </summary>
    [Fact]
    public void OtherFailures_DoNotBorrowTheAuthorizationHint()
    {
        var error = DesktopCapabilityError.UnsupportedCapability("browser.context.create");

        var result = BrowserCapabilityFailure.From(error, "browser_tabs_failed", "stale_page_version");

        var payload = string.Concat(result.Output, " | ", result.Error);

        Assert.False(result.Success);
        Assert.Contains("browser_unsupported", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("page_not_authorized", payload, StringComparison.Ordinal);
    }
}
