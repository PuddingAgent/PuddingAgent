using System.Text.Json;
using Pudding.DesktopService;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;
using PuddingDesktop.Hosting;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// 接管/暂停的**执行闸门**收敛（审计 2026-10-08 结论 #1）。
///
/// 被钉住的事实：进程内曾有三份互不相通的接管表示，产品 Shell 只写最不具执行权威的那一份
/// （浏览器工作区控制器的 UI 状态机），而 Bridge 路径的命令闸门读的是
/// <c>BrowserBridgeCommandDispatcher</c> **自己的字段** ⇒ 用户点「人工接管」对 Agent 没有约束力。
///
/// 现在的语义：控制权只有**一个写入点**（`BrowserWorkspaceController.SetUserTakeoverAsync` /
/// `SetPausedAsync`），它把状态推给所有已注册的执行闸门。
/// </summary>
public sealed class BrowserTakeoverGateTests
{
    private static BrowserBridgeCommand GotoCommand() => new()
    {
        OperationId = Guid.NewGuid(),
        Name = BrowserBridgeCommandNames.PageGoto,
        PageId = "page-99",
        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
    };

    private static (BrowserWorkspaceController Controller, BrowserBridgeCommandDispatcher Dispatcher, BrowserAutomationGateSync Gates)
        CreateWired()
    {
        var gates = new BrowserAutomationGateSync();
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());
        gates.Register(dispatcher);

        var controller = new BrowserWorkspaceController(
            new FakeBrowserRuntime(), new FakeBrowserSurfaceHost(), new FakeUiDispatcher(), gates);

        return (controller, dispatcher, gates);
    }

    [Fact]
    public async Task ShellTakeover_ReachesTheBridgeGate_AndRejectsAgentCommands()
    {
        // 这就是审计结论 #1 的验收：走**用户会走的那条路**（控制器）设置接管，
        // Bridge 路径上的命令必须被拒绝 —— 之前它读自己的字段，始终是 false。
        var (controller, dispatcher, _) = CreateWired();

        await controller.SetUserTakeoverAsync(true, CancellationToken.None);

        Assert.True(dispatcher.IsUserTakeover, "接管必须到达 Bridge 执行闸门");

        var result = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, result.ErrorCode);
    }

    [Fact]
    public async Task ReleasingTakeover_ReopensTheBridgeGate()
    {
        var (controller, dispatcher, _) = CreateWired();

        await controller.SetUserTakeoverAsync(true, CancellationToken.None);
        await controller.SetUserTakeoverAsync(false, CancellationToken.None);

        Assert.False(dispatcher.IsUserTakeover);
        var result = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Pause_AlsoReachesTheBridgeGate()
    {
        var (controller, dispatcher, _) = CreateWired();

        await controller.SetPausedAsync(true, CancellationToken.None);

        Assert.True(dispatcher.IsPaused);
        var result = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserPaused, result.ErrorCode);
    }

    [Fact]
    public async Task RegisteringAGateAfterTakeover_AlignsItImmediately()
    {
        // 能力通道可能在用户已经接管**之后**才启动 ⇒ 新闸门必须立刻对齐，
        // 否则它会停在构造时的默认值上，接管在那个路径上就是失效的。
        var (controller, dispatcher, gates) = CreateWired();
        await controller.SetUserTakeoverAsync(true, CancellationToken.None);

        var late = new RecordingGate();
        controller.RegisterAutomationGate(late);

        Assert.True(late.UserTakeover);
        Assert.Equal(2, gates.GateCount);
        Assert.True(dispatcher.IsUserTakeover);
    }

    [Fact]
    public async Task UnregisteringAGate_StopsPushingToIt()
    {
        var (controller, _, gates) = CreateWired();
        var extra = new RecordingGate();
        controller.RegisterAutomationGate(extra);

        Assert.True(controller.UnregisterAutomationGate(extra));
        await controller.SetUserTakeoverAsync(true, CancellationToken.None);

        Assert.False(extra.UserTakeover, "撤销后不应再收到控制状态");
        Assert.Equal(1, gates.GateCount);
    }

    [Fact]
    public void GateSync_FansOutToEveryRegisteredGate()
    {
        var gates = new BrowserAutomationGateSync();
        var first = new RecordingGate();
        var second = new RecordingGate();
        gates.Register(first);
        gates.Register(second);

        gates.ApplyUserTakeover(true);
        gates.ApplyPaused(true);

        Assert.True(first.UserTakeover && first.Paused);
        Assert.True(second.UserTakeover && second.Paused);
    }

    [Fact]
    public void GateSync_IsIdempotentForTheSameInstance()
    {
        var gates = new BrowserAutomationGateSync();
        var gate = new RecordingGate();

        gates.Register(gate);
        gates.Register(gate);

        Assert.Equal(1, gates.GateCount);
    }

    [Fact]
    public void GateSync_AFaultyGateDoesNotBlockTheOthers()
    {
        // 接管是安全动作：一个坏闸门不得让接管在其余路径上静默失效。
        var gates = new BrowserAutomationGateSync();
        var faulty = new RecordingGate { ThrowOnWrite = true };
        var healthy = new RecordingGate();
        gates.Register(faulty);
        gates.Register(healthy);

        gates.ApplyUserTakeover(true);

        Assert.True(healthy.UserTakeover);
    }

    [Fact]
    public void GateSync_RejectsNullArguments()
    {
        var gates = new BrowserAutomationGateSync();
        Assert.Throws<ArgumentNullException>(() => gates.Register(null!));
        Assert.Throws<ArgumentNullException>(() => gates.Unregister(null!));
        Assert.Throws<ArgumentNullException>(() => gates.ApplyTo(null!, true, false));
    }

    [Fact]
    public void Controller_RejectsNullGate()
    {
        var (controller, _, _) = CreateWired();
        Assert.Throws<ArgumentNullException>(() => controller.RegisterAutomationGate(null!));
    }

    [Fact]
    public void ControllerWithoutGateSync_DoesNotThrow()
    {
        // 旧构造路径（归档视图、既有用例）没有闸门集合：控制权写入必须仍然可用。
        var controller = new BrowserWorkspaceController(
            new FakeBrowserRuntime(), new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        Assert.False(controller.UnregisterAutomationGate(new RecordingGate()));
        controller.RegisterAutomationGate(new RecordingGate());
    }

    // ── 能力通道的闸门：把 DesktopInteractionState 接进同一个写入点 ──────────

    [Fact]
    public void DelegateGate_MapsTakeoverAndPauseOntoTheInteractionState()
    {
        var state = new DesktopInteractionState();
        var gate = new DelegateAutomationGate(state.SetUserTakeover, state.SetPaused);

        gate.SetUserTakeover(true);
        Assert.True(state.IsUserTakeover);

        gate.SetPaused(true);
        Assert.True(state.IsPaused);

        gate.SetUserTakeover(false);
        gate.SetPaused(false);
        Assert.False(state.IsUserTakeover);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public async Task CapabilityChannelGate_ReceivesTheSameControlStateAsTheBridgeGate()
    {
        // 两条传输必须同时失效：否则用户接管后，走另一条路的 Agent 动作照旧执行。
        var (controller, dispatcher, _) = CreateWired();
        var interaction = new DesktopInteractionState();
        controller.RegisterAutomationGate(
            new DelegateAutomationGate(interaction.SetUserTakeover, interaction.SetPaused));

        await controller.SetUserTakeoverAsync(true, CancellationToken.None);

        Assert.True(dispatcher.IsUserTakeover);
        Assert.True(interaction.IsUserTakeover);
    }

    [Fact]
    public void DelegateGate_RejectsNullDelegates()
    {
        Assert.Throws<ArgumentNullException>(() => new DelegateAutomationGate(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new DelegateAutomationGate(_ => { }, null!));
    }

    private sealed class RecordingGate : IBrowserAutomationGate
    {
        public bool UserTakeover { get; private set; }

        public bool Paused { get; private set; }

        public bool ThrowOnWrite { get; init; }

        public void SetUserTakeover(bool takenOver)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("gate is broken");
            }

            UserTakeover = takenOver;
        }

        public void SetPaused(bool paused)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("gate is broken");
            }

            Paused = paused;
        }
    }
}
