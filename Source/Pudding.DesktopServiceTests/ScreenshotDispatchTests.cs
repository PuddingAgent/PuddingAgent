using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Xunit;

namespace DesktopServiceTests;

/// <summary>
/// 截图能力的**分发级**用例：证明 <c>DesktopCapability.BrowserScreenshot</c> 被分发到**截图入口**，
/// 而不是落回快照分支（曾因两个 <c>case</c> 标签共用分支体导致"来了也永远不执行截图"）。
/// </summary>
public sealed class ScreenshotDispatchTests
{
    [Fact]
    public async Task Screenshot_DispatchesToTheScreenshotSurface_NotTheSnapshotPath()
    {
        var harness = ServiceHarness.Create(
            allowed: DesktopCapability.BrowserScreenshot, hasThreadAccess: true);
        var target = ServiceHarness.AgentPage;
        harness.Targets.RegisterPage(target, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserScreenshot,
            DesktopCapabilityRequest.ForScreenshot(
                new BrowserScreenshotRequest(target, DesktopPageVersion.Require(1))));

        // 替身对截图返回明确失败；关键是**分发到哪**，不是成功与否。
        Assert.True(response.IsFailure);
        Assert.Equal(1, harness.Surface.ScreenshotCount);
        Assert.Equal(0, harness.Surface.SnapshotCount);
    }
}