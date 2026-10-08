using Pudding.Contracts;
using Pudding.Contracts.Desktop;

using Pudding.DesktopService;
using PuddingBrowser.Automation;

namespace DesktopServiceTests;

/// <summary>
/// 接管/暂停的事实源是进程内唯一权威实例（设计方案 §3.1）；
/// 状态对象只做读投影，因此每个用例都要有真实的 authority 而不是私有 bool。
/// </summary>
internal static class TestInteractionStates
{
    internal static DesktopInteractionState New() => new(new BrowserAutomationAuthority(
        new DesktopInstanceId("desk-test"), new DesktopProcessInstanceId("proc-test")));
}

/// <summary>
/// 控制面接宿主（设计方案 §3.1）：接管/暂停的**唯一事实源**是进程内那一个
/// <see cref="BrowserAutomationAuthority"/>，<see cref="DesktopInteractionState"/> 只是读投影。
/// 这些用例钉的正是「投影」本身：写进去能从权威读到，直接写权威也能从状态读到。
/// </summary>
public sealed class AuthorityProjectionTests
{
    [Fact]
    public void State_Writes_Land_In_The_Single_Authority()
    {
        var state = TestInteractionStates.New();

        state.NotifyUserTakeover("user clicked");

        var snapshot = state.Authority.Capture();
        Assert.True(snapshot.IsUserTakeover);
        Assert.Equal("user clicked", snapshot.Reason);
        // 世代必须推进：接管前的队列/租约不得复活（方案 G1）。
        Assert.NotEqual(
            BrowserControlEpoch.Initial.Value,
            snapshot.ControlEpoch.Value);
    }

    [Fact]
    public void State_Reads_The_Authority_Not_A_Private_Flag()
    {
        // 绕过状态对象直接写权威（Shell 的接管开关或页面生命周期端口会这么做）：
        // 状态必须立刻反映出来 —— 否则就是又一份私有状态。
        var state = TestInteractionStates.New();

        state.Authority.SetPaused(true, "runtime paused");
        state.Authority.SetUserTakeover(true, "takeover");

        Assert.True(state.IsPaused);
        Assert.True(state.IsUserTakeover);

        state.Authority.SetUserTakeover(false);
        Assert.False(state.IsUserTakeover);
        Assert.True(state.IsPaused, "解除接管不得连带解除暂停（两轴独立）");
    }

    [Fact]
    public async Task Takeover_WrittenDirectlyToTheAuthority_StillBlocksMutatingCalls()
    {
        // 最强的投影断言：**不经过** DesktopInteractionState 写权威，准入也必须拦下变更类能力。
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Service.Authority.SetUserTakeover(true, "direct write");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UserTakeover, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public void Authority_IsRequired_FailClosed()
    {
        // 没有权威实例就没有唯一事实源：宁可构造失败，也不要退化成"各自维护标志"。
        Assert.Throws<ArgumentNullException>(() => new DesktopInteractionState(null!));
    }

    [Fact]
    public async Task WindowClose_Closes_TheAuthority()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        harness.Service.Close();

        var snapshot = harness.Service.Authority.Capture();
        Assert.True(snapshot.IsClosed);
        Assert.Contains("window", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}

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
        var state = TestInteractionStates.New();
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
        var state = TestInteractionStates.New();
        state.Pause("   ");
        Assert.Equal("unspecified", state.Reason);
    }

    [Fact]
    public void BooleanSetters_AreTheExecutionGateFormOfTheSameState()
    {
        // 执行闸门端口（IBrowserAutomationGate）走的是布尔形态；它必须与
        // Notify/Clear/Pause/Resume 完全等价，否则两条路径会读出不一致的控制状态。
        var state = TestInteractionStates.New();

        state.SetUserTakeover(true);
        Assert.True(state.IsUserTakeover);

        state.SetPaused(true);
        Assert.True(state.IsPaused);

        state.SetUserTakeover(false);
        Assert.False(state.IsUserTakeover);
        Assert.True(state.IsPaused, "两个轴相互独立：撤销接管不得顺手恢复暂停");

        state.SetPaused(false);
        Assert.False(state.IsPaused);
        Assert.Null(state.Reason);
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

/// <summary>交互槽位：单窗口同时最多一个对话框/Picker（第二个并发交互必须被拒绝，而不是排队）。</summary>
public sealed class InteractionSlotTests
{
    [Fact]
    public void OnlyOneInteractionCanBeActiveAtATime()
    {
        var state = TestInteractionStates.New();

        Assert.False(state.IsInteractionActive);
        Assert.True(state.TryEnterInteraction("dialog-1"));
        Assert.True(state.IsInteractionActive);
        Assert.Equal("dialog-1", state.InteractionOwner);

        // 第二个并发交互被拒绝（排队会让"取消的是哪一个"变得不可判定）。
        Assert.False(state.TryEnterInteraction("dialog-2"));
        Assert.Equal("dialog-1", state.InteractionOwner);

        // 同一持有者重复进入也算重复调用：不破坏既有状态。
        Assert.False(state.TryEnterInteraction("dialog-1"));
        Assert.Equal("dialog-1", state.InteractionOwner);
    }

    [Fact]
    public void OnlyTheOwnerCanReleaseTheSlot()
    {
        var state = TestInteractionStates.New();
        _ = state.TryEnterInteraction("dialog-1");

        Assert.False(state.ExitInteraction("dialog-2"));
        Assert.Equal("dialog-1", state.InteractionOwner);

        Assert.True(state.ExitInteraction("dialog-1"));
        Assert.False(state.IsInteractionActive);
        Assert.Null(state.InteractionOwner);

        // 已释放后再释放是空操作。
        Assert.False(state.ExitInteraction("dialog-1"));
    }

    [Fact]
    public void SlotCanBeTakenAgainAfterRelease()
    {
        var state = TestInteractionStates.New();
        _ = state.TryEnterInteraction("dialog-1");
        _ = state.ExitInteraction("dialog-1");

        Assert.True(state.TryEnterInteraction("dialog-2"));
        Assert.Equal("dialog-2", state.InteractionOwner);
    }

    [Fact]
    public void SlotRequiresAnOwner()
    {
        var state = TestInteractionStates.New();

        Assert.Throws<ArgumentException>(() => state.TryEnterInteraction("  "));
        Assert.False(state.IsInteractionActive);
    }
}

/// <summary>对话框接入服务：交互槽位强制"单窗口同时最多一个"；取消是结果而不是失败。</summary>
public sealed class DialogAdmissionTests
{
    private const DesktopCapability Allowed = DesktopCapability.ShellDialog | DesktopCapability.ShellFilePicker;

    [Fact]
    public async Task SecondConcurrentDialog_IsRejectedWhileTheFirstIsActive()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true, shellCallerTrust: DesktopContextTrust.Workbench);
        harness.Surface.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 第一个对话框占住槽位（surface 被 gate 挡住，尚未完成）。
        var first = harness.Service.ExecuteAsync(
            ServiceHarness.Descriptor(DesktopCapability.ShellDialog),
            DesktopCapabilityRequest.ForDialog(new DesktopDialogRequest("标题", "内容")),
            harness.Context("op-1"),
            CancellationToken.None);
        await TestWait.UntilAsync(() => harness.Surface.DialogCount == 1, "first dialog reaches the surface");

        // 第二个并发对话框必须被拒绝，而不是排队。
        var second = await harness.Service.ExecuteAsync(
            ServiceHarness.Descriptor(DesktopCapability.ShellDialog),
            DesktopCapabilityRequest.ForDialog(new DesktopDialogRequest("标题", "内容")),
            harness.Context("op-2"),
            CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, second.Error!.Code);
        Assert.Equal(1, harness.Surface.DialogCount);

        harness.Surface.Gate.SetResult();
        Assert.False((await first).IsFailure);
    }

    [Fact]
    public async Task CanceledDialog_IsAResultNotAFailure()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true, shellCallerTrust: DesktopContextTrust.Workbench);
        harness.Surface.DialogHandler = (_, _) => Task.FromResult(
            CapabilityResult<DesktopDialogResult>.Success(new DesktopDialogResult(DesktopDialogChoice.Cancel)));

        var response = await harness.Service.ExecuteAsync(
            ServiceHarness.Descriptor(DesktopCapability.ShellDialog),
            DesktopCapabilityRequest.ForDialog(new DesktopDialogRequest("标题", "内容", DesktopDialogButtons.OkCancel)),
            harness.Context("op-1"),
            CancellationToken.None);

        Assert.False(response.IsFailure);          // 取消不是失败
        Assert.True(response.Dialog!.Canceled);
        Assert.False(response.Dialog.IsAffirmative);

        // 终态必须释放槽位：再来一个对话框应当能进入。
        var again = await harness.Service.ExecuteAsync(
            ServiceHarness.Descriptor(DesktopCapability.ShellDialog),
            DesktopCapabilityRequest.ForDialog(new DesktopDialogRequest("标题", "内容")),
            harness.Context("op-2"),
            CancellationToken.None);
        Assert.False(again.IsFailure);
        Assert.False(harness.Service.Interaction.IsInteractionActive);
    }

    [Fact]
    public async Task DialogFailure_StillReleasesTheSlot()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true, shellCallerTrust: DesktopContextTrust.Workbench);
        harness.Surface.DialogHandler = (_, _) => Task.FromResult(
            CapabilityResult<DesktopDialogResult>.Failure(DesktopCapabilityError.UiUnavailable("no window")));

        var response = await harness.Service.ExecuteAsync(
            ServiceHarness.Descriptor(DesktopCapability.ShellDialog),
            DesktopCapabilityRequest.ForDialog(new DesktopDialogRequest("标题", "内容")),
            harness.Context("op-1"),
            CancellationToken.None);

        Assert.True(response.IsFailure);
        // 异常/失败路径也必须释放槽位，否则后续对话框永远进不来。
        Assert.False(harness.Service.Interaction.IsInteractionActive);
    }
}
}
