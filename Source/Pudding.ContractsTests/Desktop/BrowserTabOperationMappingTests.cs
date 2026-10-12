using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// §5.3 **request-aware** 映射：同一能力（`browser.tabs`）下的**具体动作**最小准入不同 ⇒
/// `new` / `activate` / `close` 必须**分别**映射。
///
/// 为什么单独钉这一条：旧实现"按最严的那个映射"（一律 `TabClose`），于是 `tabs.activate` 被错误地
/// 要求管理级页面授权（`Manage|Read`），而它实际只需**目标页 Write**（2026-10-12 外部复核定位）。
/// </summary>
public sealed class BrowserTabOperationMappingTests
{
    private static readonly DesktopPageTarget Target = new("ctx", "page");

    /// <summary>按动作构造合法请求：**新建**没有已存在页面（契约规定 target 为 null），其余需钉版本。</summary>
    private static DesktopCapabilityRequest Request(DesktopTabAction action) =>
        DesktopCapabilityRequest.ForTabs(
            action == DesktopTabAction.New
                ? BrowserTabsRequest.New("ctx")
                : new BrowserTabsRequest(Target, action, DesktopPageVersion.Require(1)));

    [Theory]
    [InlineData(DesktopTabAction.New, BrowserAutomationOperation.TabNew)]
    [InlineData(DesktopTabAction.Activate, BrowserAutomationOperation.TabActivate)]
    [InlineData(DesktopTabAction.Close, BrowserAutomationOperation.TabClose)]
    public void TabActionsMapSeparately(DesktopTabAction action, BrowserAutomationOperation expected)
    {
        Assert.True(DesktopCapabilityOperations.TryGetBrowserOperation(Request(action), out var operation));
        Assert.Equal(expected, operation);
    }

    [Fact]
    public void TabActivateIsNotDowngradedToTabClose()
    {
        Assert.True(DesktopCapabilityOperations.TryGetBrowserOperation(
            Request(DesktopTabAction.Activate), out var activateOperation));
        Assert.True(DesktopCapabilityOperations.TryGetBrowserOperation(
            Request(DesktopTabAction.Close), out var closeOperation));

        Assert.Equal(BrowserAutomationOperation.TabActivate, activateOperation);
        Assert.NotEqual(closeOperation, activateOperation);
    }

    [Fact]
    public void CapabilityLevelMappingStillWorksForCallersThatOnlyHaveACapability()
    {
        // 既有调用点（只有能力、没有请求）必须继续可用；请求级调用会**覆盖**它（见上两条）。
        // 这不是"第二份策略"：同一映射的分层入口，且请求级优先（DesktopService.ValidateAdmission）。
        Assert.True(DesktopCapabilityOperations.TryGetBrowserOperation(
            DesktopCapability.BrowserTabs, out var capabilityLevel));
        Assert.Equal(BrowserAutomationOperation.TabClose, capabilityLevel);
    }
}
