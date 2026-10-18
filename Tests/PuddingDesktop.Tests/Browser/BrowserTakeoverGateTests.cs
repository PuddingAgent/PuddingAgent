using System.Text.Json;
using Pudding.DesktopService;
using PuddingBrowser.Automation;
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
    /// <summary>
    /// 控制面的事实源是进程内唯一权威实例（设计方案 §3.1）：闸门写的是它，
    /// <c>DesktopInteractionState</c> 只是读投影。
    /// </summary>
    private static DesktopInteractionState NewInteractionState() => new(new BrowserAutomationAuthority(
        new Pudding.Contracts.DesktopInstanceId("desk-test"),
        new Pudding.Contracts.DesktopProcessInstanceId("proc-test")));

    private static BrowserBridgeCommand GotoCommand() => new()
    {
        OperationId = Guid.NewGuid(),
        Name = BrowserBridgeCommandNames.PageGoto,
        ContextId = "ctx",
        PageId = "page",
        Origin = BrowserTestState.Origin(),
        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "agent-a", TaskId = "run-agent-a" },
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
    };

    private static (BrowserWorkspaceController Controller, BrowserBridgeCommandDispatcher Dispatcher, BrowserAutomationGateSync Gates)
        CreateWired()
    {
        var gates = new BrowserAutomationGateSync();
        var dispatcher = BrowserTestState.CreateDispatcher();
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
        var (_, dispatcher, controller) = CreateAuthorized();

        await controller.SetUserTakeoverAsync(true, CancellationToken.None);
        await controller.SetUserTakeoverAsync(false, CancellationToken.None);

        Assert.False(dispatcher.IsUserTakeover);
        var result = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);
        Assert.True(result.Success, $"error={result.ErrorCode}: {result.ErrorMessage}");
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

    // ── F2：暂停/接管是运行控制，只阻断副作用；只读观察必须继续 ────────────

    /// <summary>
    /// 配对反例（方案 §6）：接管时 **snapshot 成功、fill 被阻止**。
    /// 修复前派发器在识别命令前就拒绝全部命令 ⇒ 只读也被拒，"接管后可读"只在文档里成立。
    /// </summary>
    [Fact]
    public async Task Takeover_BlocksWrites_ButKeepsReads()
    {
        var (_, dispatcher, controller) = CreateAuthorized();

        await controller.SetUserTakeoverAsync(true, CancellationToken.None);
        Assert.True(dispatcher.IsUserTakeover);

        var read = await dispatcher.DispatchAsync(SnapshotCommand(), CancellationToken.None);
        Assert.True(read.Success, $"接管期间只读观察必须继续：{read.ErrorCode}: {read.ErrorMessage}");

        var write = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);
        Assert.False(write.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, write.ErrorCode);
    }

    [Fact]
    public async Task Pause_BlocksWrites_ButKeepsReads()
    {
        var (_, dispatcher, controller) = CreateAuthorized();

        await controller.SetPausedAsync(true, CancellationToken.None);
        Assert.True(dispatcher.IsPaused);

        var read = await dispatcher.DispatchAsync(SnapshotCommand(), CancellationToken.None);
        Assert.True(read.Success, $"暂停期间只读观察必须继续：{read.ErrorCode}: {read.ErrorMessage}");

        var write = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);
        Assert.False(write.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserPaused, write.ErrorCode);
    }

    [Fact]
    public async Task Takeover_BlocksMutatingInteract()
    {
        var (_, dispatcher, controller) = CreateAuthorized();

        await controller.SetUserTakeoverAsync(true, CancellationToken.None);

        var interact = await dispatcher.DispatchAsync(InteractCommand(), CancellationToken.None);
        Assert.False(interact.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, interact.ErrorCode);
    }

    private static (Pudding.DesktopService.BrowserPageLifecycle Lifecycle, BrowserBridgeCommandDispatcher Dispatcher, BrowserWorkspaceController Controller)
        CreateAuthorized()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var gates = new BrowserAutomationGateSync();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());
        gates.Register(dispatcher);

        var controller = new BrowserWorkspaceController(
            new FakeBrowserRuntime(), new FakeBrowserSurfaceHost(), new FakeUiDispatcher(), gates);

        // 正向夹具：用户已把页面交出；归属由第一条带可信 Origin 的命令确认（ADR-097 D3）。
        BrowserTestState.HandOver(lifecycle);
        return (lifecycle, dispatcher, controller);
    }

    private static BrowserBridgeCommand SnapshotCommand() => new()
    {
        OperationId = Guid.NewGuid(),
        Name = BrowserBridgeCommandNames.PageSnapshot,
        ContextId = "ctx",
        PageId = "page",
        Origin = BrowserTestState.Origin(),
        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "agent-a", TaskId = "run-agent-a" },
        Arguments = JsonSerializer.SerializeToElement(new { }),
    };

    private static BrowserBridgeCommand InteractCommand() => new()
    {
        OperationId = Guid.NewGuid(),
        Name = BrowserBridgeCommandNames.PageInteract,
        ContextId = "ctx",
        PageId = "page",
        Origin = BrowserTestState.Origin(),
        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "agent-a", TaskId = "run-agent-a" },
        Arguments = JsonSerializer.SerializeToElement(new
        {
            action = "click",
            locator = new { kind = "css", value = "button" },
        }),
    };

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
        var state = NewInteractionState();
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
        var interaction = NewInteractionState();
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

    // ── Bridge 闸门不再自己持有标志：读写的是同一个权威实例（结论 #1 收尾）──────

    [Fact]
    public void BridgeDispatcher_ReadsTheSharedAuthority_NotItsOwnFlag()
    {
        // 用**不经过调度器**的路径写控制状态（Shell 的接管开关、生命周期端口都会这么做），
        // 调度器必须立刻反映出来 —— 否则它又是"自己的一个字段"，人工接管对 Bridge 命令没有约束力。
        var lifecycle = BrowserTestState.CreateLifecycle();
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);

        lifecycle.SetUserTakeover(true, "user clicked");

        Assert.True(dispatcher.IsUserTakeover);
        Assert.False(dispatcher.IsPaused);
    }

    [Fact]
    public async Task BridgeDispatcher_RejectsCommands_WhenTheSharedAuthorityIsTakenOver()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        lifecycle.SetUserTakeover(true, "user clicked");

        var result = await dispatcher.DispatchAsync(GotoCommand(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, result.ErrorCode);
    }

    [Fact]
    public void BridgeDispatcher_WritesTheSharedAuthority()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);

        dispatcher.SetPaused(true);
        dispatcher.SetUserTakeover(true);

        // 同一实例：能力通道与生命周期清理点读到的就是这两个值。
        Assert.True(lifecycle.IsPaused);
        Assert.True(lifecycle.IsUserTakeover);

        dispatcher.SetPaused(false);
        Assert.False(lifecycle.IsPaused);
        Assert.True(lifecycle.IsUserTakeover, "解除暂停不得连带解除接管（两轴独立）");
    }

    [Fact]
    public void Constructor_RejectsMissingLifecyclePort()
    {
        Assert.Throws<ArgumentNullException>(() => new BrowserBridgeCommandDispatcher(null!));
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
