using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>只读 Shell 状态：surface 报告窗口/托盘，DesktopService 补齐自动化状态与页面数。</summary>
public sealed class ShellStatusTests
{
    private const DesktopCapability Allowed =
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.ShellNotification
        | DesktopCapability.ShellStatus;

    [Fact]
    public async Task ShellStatus_ComesFromTheSurfaceAndIsEnrichedByTheService()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, DesktopCapabilityRequest.ForShellStatus());

        Assert.False(response.IsFailure);
        // 窗口/托盘来自 surface（默认 HiddenToTray/true）。
        Assert.Equal(DesktopWindowState.HiddenToTray, response.ShellStatus!.WindowState);
        Assert.True(response.ShellStatus.TrayVisible);
        // 自动化状态与页面数来自本服务的权威状态，而不是 surface 自报。
        Assert.Equal(DesktopAutomationState.Free, response.ShellStatus.Automation);
        Assert.Equal(harness.Service.Targets.OpenPageCount, response.ShellStatus.OpenPageCount);
        Assert.True(response.ShellStatus.OpenPageCount > 0);
        Assert.Equal(1, harness.Surface.ShellStatusCount);
    }

    [Fact]
    public async Task ShellStatus_ReportsPausedAndUserTakeoverStates()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        harness.Service.Interaction.Pause("maintenance");
        var paused = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, DesktopCapabilityRequest.ForShellStatus());
        Assert.Equal(DesktopAutomationState.Paused, paused.ShellStatus!.Automation);

        harness.Service.Interaction.Resume();
        harness.Service.Interaction.NotifyUserTakeover("user is driving");
        var takeover = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, DesktopCapabilityRequest.ForShellStatus());
        Assert.Equal(DesktopAutomationState.UserTakeover, takeover.ShellStatus!.Automation);
    }

    [Fact]
    public async Task ShellStatus_PropagatesSurfaceFailureWithoutInventingData()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        harness.Surface.ShellStatusHandler = _ => Task.FromResult(
            CapabilityResult<DesktopShellStatus>.Failure(
                DesktopCapabilityError.Disconnected("shell is closing")));

        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, DesktopCapabilityRequest.ForShellStatus());

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Disconnected, response.Error!.Code);
    }

    [Fact]
    public async Task ShellStatus_RequiresItsPayloadAndAcceptsAnUntrustedCaller()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        // 只读状态不涉及页面：即使调用方可信级别为 Untrusted 也应放行。
        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, DesktopCapabilityRequest.ForShellStatus());
        Assert.False(response.IsFailure);

        // 载荷不匹配 ⇒ 明确 invalid_request，且不触碰 surface。
        var mismatched = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, harness.NavigateRequest(ServiceHarness.AgentPage));
        Assert.True(mismatched.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, mismatched.Error!.Code);
        Assert.Equal(1, harness.Surface.ShellStatusCount);
    }

    [Fact]
    public async Task ShellStatus_IsRejectedWhenNotAllowed()
    {
        var harness = ServiceHarness.Create(
            allowed: DesktopCapability.WebViewNavigate | DesktopCapability.ShellNotification,
            hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellStatus, DesktopCapabilityRequest.ForShellStatus());

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, response.Error!.Code);
        Assert.Equal(0, harness.Surface.ShellStatusCount);
    }
}
