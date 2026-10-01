using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>只读 Shell 状态：输入校验、线名冻结与 fail-safe 折叠。</summary>
public sealed class ShellStatusTests
{
    [Fact]
    public void ShellStatus_ValidatesItsInputs()
    {
        _ = new DesktopShellStatus(DesktopWindowState.Visible, false, DesktopAutomationState.Free, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopShellStatus(DesktopWindowState.Visible, false, DesktopAutomationState.Free, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopShellStatus((DesktopWindowState)99, false, DesktopAutomationState.Free, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopShellStatus(DesktopWindowState.Visible, false, (DesktopAutomationState)99, 0));
    }

    [Fact]
    public void WindowStateLineNames_AreFrozenAndUnknownFoldsToUnknown()
    {
        Assert.Equal(
            ["unknown", "visible", "hidden_to_tray", "closing"],
            Enum.GetValues<DesktopWindowState>().Select(DesktopShellStatusWire.NameOf).ToArray());

        Assert.Equal(DesktopWindowState.HiddenToTray, DesktopShellStatusWire.ParseWindowState("hidden_to_tray"));
        Assert.Equal(DesktopWindowState.Unknown, DesktopShellStatusWire.ParseWindowState("minimized"));
        Assert.Equal(DesktopWindowState.Unknown, DesktopShellStatusWire.ParseWindowState(null));
    }

    [Fact]
    public void AutomationStateLineNames_AreFrozenAndUnknownFoldsToUnsafeSide()
    {
        Assert.Equal(
            ["free", "paused", "user_takeover"],
            Enum.GetValues<DesktopAutomationState>().Select(DesktopShellStatusWire.NameOf).ToArray());

        Assert.Equal(DesktopAutomationState.Paused, DesktopShellStatusWire.ParseAutomationState("paused"));
        Assert.Equal(DesktopAutomationState.UserTakeover, DesktopShellStatusWire.ParseAutomationState("user_takeover"));

        // fail safe：不认识的自动化状态按「用户接管」处理，宁可少自动，不可误自动。
        Assert.Equal(DesktopAutomationState.UserTakeover, DesktopShellStatusWire.ParseAutomationState("hibernating"));
        Assert.Equal(DesktopAutomationState.UserTakeover, DesktopShellStatusWire.ParseAutomationState(null));
    }

    [Fact]
    public void ShellStatus_IsImmutableValueType()
    {
        var left = new DesktopShellStatus(DesktopWindowState.Visible, true, DesktopAutomationState.Free, 2);
        var right = new DesktopShellStatus(DesktopWindowState.Visible, true, DesktopAutomationState.Free, 2);
        var other = new DesktopShellStatus(DesktopWindowState.Visible, true, DesktopAutomationState.Free, 3);

        Assert.Equal(left, right);
        Assert.NotEqual(left, other);
        Assert.Equal("Visible/tray=True/Free/pages=2", left.ToString());
    }

    [Fact]
    public void RequestUnion_CarriesTheParameterlessShellStatusQuery()
    {
        var request = DesktopCapabilityRequest.ForShellStatus();

        Assert.True(request.ShellStatus);
        Assert.Null(request.Target);
        Assert.Null(request.Navigate);
        Assert.Null(request.Javascript);
        Assert.Null(request.Notification);
        Assert.Null(request.PageState);
        Assert.Equal(DesktopPageVersion.Unknown, request.ExpectedPageVersion);
        Assert.Equal("shell_status", request.ToString());
    }

    [Fact]
    public void ResponseUnion_CarriesTheShellStatusOutcome()
    {
        var status = new DesktopShellStatus(DesktopWindowState.HiddenToTray, true, DesktopAutomationState.Paused, 4);
        var response = DesktopCapabilityResponse.FromShellStatus(status);

        Assert.False(response.IsFailure);
        Assert.Equal(status, response.ShellStatus);
        Assert.Null(response.Navigate);
        Assert.Null(response.Javascript);
        Assert.Null(response.Notification);
        Assert.Null(response.PageState);
        Assert.Contains("shell_status", response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PageReadinessLineNames_LiveInContractsAndFoldUnknown()
    {
        Assert.Equal(
            ["unknown", "loading", "interactive", "complete", "failed"],
            Enum.GetValues<DesktopPageReadiness>().Select(DesktopPageReadinessWire.NameOf).ToArray());
        Assert.Equal(DesktopPageReadiness.Interactive, DesktopPageReadinessWire.Parse("interactive"));
        Assert.Equal(DesktopPageReadiness.Unknown, DesktopPageReadinessWire.Parse("hibernated"));
        Assert.Equal(DesktopPageReadiness.Unknown, DesktopPageReadinessWire.Parse(null));
    }
}
