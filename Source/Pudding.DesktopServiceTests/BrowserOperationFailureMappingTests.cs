using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Abstractions;

namespace DesktopServiceTests;

/// <summary>
/// 驱动稳定码 → 能力错误码的翻译表：跨进程时**先在这里翻译**，工具层随后按
/// <c>browser_*</c> 码给修复提示。
///
/// 为什么钉死它：工具侧另有一张同义表（<c>BrowserOperationFailure.Map</c>），
/// 同一个码在两处若给出不同语义，调用方看到的行为就会随部署形态变化
/// （进程内直连走工具表，Desktop 托管走这张表）。实测分歧：
/// <c>browser_element_disabled</c> 在这里被归为 <c>InvalidTarget</c>、在工具表里归为
/// <c>InvalidRequest</c>，于是禁用按钮的拒绝最终报成 <c>browser_page_not_found</c>
/// （"页面找不到"）——与事实相反，会把调用方引向重新定位页面。
/// 本测试固定当前语义，任一侧改动都必须同步改动这里。
/// </summary>
public sealed class BrowserOperationFailureMappingTests
{
    [Theory]
    // 定位歧义：绝不静默选第一个，也绝不重试（同样的定位一定再次歧义）。
    [InlineData("browser_locator_ambiguous", DesktopCapabilityErrorCode.InvalidRequest, false)]
    // 参数错：什么都没派发，改参数即可。
    [InlineData("browser_invalid_arguments", DesktopCapabilityErrorCode.InvalidRequest, false)]
    // 零命中可能只是页面还没渲染完 ⇒ 允许等待后重试。
    [InlineData("browser_element_not_found", DesktopCapabilityErrorCode.InvalidTarget, true)]
    // 以下四种是"命中但不可操作"：同一次请求再发一次不会变好，因此不可自动重试。
    [InlineData("browser_element_not_visible", DesktopCapabilityErrorCode.InvalidTarget, false)]
    [InlineData("browser_element_not_in_viewport", DesktopCapabilityErrorCode.InvalidTarget, false)]
    [InlineData("browser_element_obstructed", DesktopCapabilityErrorCode.InvalidTarget, false)]
    [InlineData("browser_element_disabled", DesktopCapabilityErrorCode.InvalidTarget, false)]
    // 能力缺失是"这条路不存在"，不是"这次失败"。
    [InlineData("browser_operation_not_supported", DesktopCapabilityErrorCode.UnsupportedCapability, false)]
    // 引用过期必须与"没找到"分开：唯一正确动作是重新观察页面，重试同一个 ref 必定再失败。
    [InlineData("stale_element_reference", DesktopCapabilityErrorCode.PageVersionMismatch, false)]
    [InlineData("page_version_mismatch", DesktopCapabilityErrorCode.PageVersionMismatch, false)]
    public void DriverCodesMapToStableSemantics(string code, DesktopCapabilityErrorCode expected, bool retryable)
    {
        var error = DesktopService.MapBrowserOperationFailure(new BrowserOperationException(code, "detail text"));

        Assert.Equal(expected, error.Code);
        Assert.Equal(retryable, error.Retryable);
        // 稳定码必须原样进入消息：工具侧那张按 browser_* 码写的提示表只能靠它命中。
        Assert.StartsWith(code + ": ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnregisteredDriverCodeIsInternalAndDoesNotLeakDriverMessage()
    {
        var error = DesktopService.MapBrowserOperationFailure(
            new BrowserOperationException("browser_something_new", "page content that must not leak"));

        Assert.Equal(DesktopCapabilityErrorCode.InternalError, error.Code);
        Assert.DoesNotContain("page content", error.Message, StringComparison.Ordinal);
        Assert.Contains("browser_something_new", error.Message, StringComparison.Ordinal);
    }
}
