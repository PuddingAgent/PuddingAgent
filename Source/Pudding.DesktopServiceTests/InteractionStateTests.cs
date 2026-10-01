using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>暂停与用户接管：只读能力保持可用，变更类能力必须明确拒绝。</summary>
public sealed class InteractionStateTests
{
    [Fact]
    public async Task Paused_RejectsMutatingCapability_WithPaused()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Interaction.Pause("user pressed pause");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.Paused, response.Error!.Code);
        Assert.True(response.Error.Retryable);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task Paused_KeepsReadOnlyCapabilityAvailable()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Interaction.Pause("paused");

        var state = await harness.Service.GetPageStateAsync(harness.Context(), ServiceHarness.AgentPage);

        Assert.True(state.IsSuccess);
        Assert.Equal(1, harness.Surface.PageStateCount);
    }

    [Fact]
    public async Task Paused_KeepsNotificationAvailable()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Interaction.Pause("paused");

        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellNotification, harness.NotificationRequest());

        Assert.True(response.Notification!.Shown);
    }

    [Fact]
    public async Task UserTakeover_RejectsMutatingCapability_WithUserTakeover()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Interaction.NotifyUserTakeover("user clicked in the page");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript, harness.JavascriptRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UserTakeover, response.Error!.Code);
        Assert.Equal(0, harness.Surface.JavascriptCount);
    }

    [Fact]
    public async Task UserTakeover_TakesPrecedenceOverPaused()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Interaction.Pause("paused");
        harness.Service.Interaction.NotifyUserTakeover("takeover");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UserTakeover, response.Error!.Code);
    }

    [Fact]
    public async Task ResumeAndClearTakeover_RestoreAutomation()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Interaction.Pause("paused");
        harness.Service.Interaction.NotifyUserTakeover("takeover");

        harness.Service.Interaction.Resume();
        Assert.True(harness.Service.Interaction.IsUserTakeover);
        Assert.False(harness.Service.Interaction.IsPaused);

        harness.Service.Interaction.ClearUserTakeover();

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.NotNull(response.Navigate);
        Assert.False(harness.Service.Interaction.IsPaused);
        Assert.False(harness.Service.Interaction.IsUserTakeover);
    }

    [Fact]
    public void InteractionReason_ReportsTheActiveAxis()
    {
        var state = new Pudding.DesktopService.DesktopInteractionState();
        Assert.Null(state.Reason);

        state.Pause("  paused by user  ");
        Assert.Equal("paused by user", state.Reason);

        state.NotifyUserTakeover("takeover");
        Assert.Equal("takeover", state.Reason);

        state.ClearUserTakeover();
        Assert.Equal("paused by user", state.Reason);

        state.Resume();
        Assert.Null(state.Reason);
    }

    [Fact]
    public void InteractionReason_FallsBackToPlaceholderForBlankInput()
    {
        var state = new Pudding.DesktopService.DesktopInteractionState();
        state.Pause("   ");
        Assert.Equal("unspecified", state.Reason);
    }

    [Fact]
    public async Task TakeoverWhileQueued_IsDetectedAfterQueueing()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));
        Assert.Equal(1, harness.Dispatcher.QueuedCount);
        Assert.False(pending.IsCompleted);

        // 用户在执行排队期间接管浏览器：拿到 UI 线程后必须复检并拒绝。
        harness.Service.Interaction.NotifyUserTakeover("takeover while queued");
        harness.Dispatcher.Pump();

        var response = await pending;
        Assert.Equal(DesktopCapabilityErrorCode.UserTakeover, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }
}
