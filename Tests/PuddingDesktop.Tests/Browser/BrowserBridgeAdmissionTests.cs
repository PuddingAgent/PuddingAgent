using System.Text.Json;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// **Bridge 准入与幂等的不变式套件**（外部复核反例迁入常规测试）。
///
/// ① **无会话授权**的命令必须在触碰 handler **之前**被拒（fail closed）；
/// ② 幂等缓存**不得跨调用者回收**（同 operationId 换调用者 ⇒ 冲突）；
/// ③ 同一 operationId 的**并发**调用只允许执行一次（后来者等待同一完成源）。
///
/// 正向配对见 <see cref="BrowserBridgeAuthorizationFixtureTests"/>：**有授权必须仍能成功** ——
/// 防止用"一律拒绝"把反例刷绿。ADR-097 D3/D4：准入由会话权威裁决（用户交页 + 宿主注入 owner）。
/// </summary>
public sealed class BrowserBridgeAdmissionTests
{
    private sealed class CountingHandler : IBrowserCommandHandler
    {
        public int Count;
        public TaskCompletionSource<bool>? Gate;

        public async Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            return new BrowserBridgeCommandResult { OperationId = command.OperationId, Success = true };
        }
    }

    private static BrowserBridgeCommand Command(Guid? id = null, string caller = "agent-a", string pageId = "page") => new()
    {
        OperationId = id ?? Guid.NewGuid(),
        ContextId = "ctx",
        PageId = pageId,
        Origin = BrowserTestState.Origin(),
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = caller, TaskId = "run-" + caller },
        Name = BrowserBridgeCommandNames.PageGoto,
        DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
    };

    [Fact]
    public async Task BridgeWithNoHandedOverPageMustRejectBeforeHandler()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        lifecycle.NotifyBridgeConnected(BrowserTestState.Generation.Value);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);
        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);
        Assert.False(result.Success, $"no handover but handler calls={handler.Count}");
        Assert.Equal("agent_target_required", result.ErrorCode);
        Assert.Equal(0, handler.Count);
    }

    // ── F3：桥在线只证明传输活性，不授予任意页面控制权 ──────────────────────

    [Fact]
    public async Task BridgeLivenessAloneDoesNotAuthorizePagesTheUserDidNotSelect()
    {
        // 桥换代/在线本身不产生可执行授权：目标必须由用户交页 + 可信 owner 确认（ADR-097 D3/D4）。
        var lifecycle = BrowserTestState.CreateLifecycle();
        lifecycle.NotifyBridgeConnected(BrowserTestState.Generation.Value);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("agent_target_required", result.ErrorCode);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task UserSelectedTargetPageIsDrivableOverTheBridge()
    {
        // 正向配对：没有 grant，但用户把这一页交给了 Agent 且命令带可信 owner ⇒ 必须执行。
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);

        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);

        Assert.True(result.Success, $"selected target must be drivable; error={result.ErrorCode}: {result.ErrorMessage}");
        Assert.Equal(1, handler.Count);
        Assert.True(lifecycle.CaptureSessionStatus().HasTarget, "第一条可信命令应确认交页归属");
    }

    [Fact]
    public async Task NonTargetPageIsRejectedEvenWhenTheBridgeIsLive()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle, pageId: "other");

        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("agent_target_required", result.ErrorCode);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ACommandWithoutTrustedOwnerIsRejectedEvenForTheSelectedPage()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);

        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        // Origin 缺失 ⇒ 无法证明调用属于哪个 Core 会话 ⇒ 在触碰 handler 之前拒绝。
        var command = Command() with { Origin = null };
        var result = await dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("browser_control_handle_required", result.ErrorCode);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ASecondCoreSessionCannotUseTheHandedOverPage()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        Assert.True((await dispatcher.DispatchAsync(Command(), CancellationToken.None)).Success);

        // 另一会话的命令：owner 不匹配 ⇒ 拒绝（无关任务不能串用交出的页面）。
        var otherOwner = Command() with { Origin = BrowserTestState.Origin("session-agent-b") };
        var denied = await dispatcher.DispatchAsync(otherOwner, CancellationToken.None);

        Assert.False(denied.Success);
        Assert.Equal("browser_owner_mismatch", denied.ErrorCode);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task CachedResultMustNotBeReplayedToAnotherCaller()
    {
        var dispatcher = BrowserTestState.CreateDispatcher();
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);
        var id = Guid.NewGuid();
        var first = await dispatcher.DispatchAsync(Command(id), CancellationToken.None);
        var second = await dispatcher.DispatchAsync(Command(id, "agent-b"), CancellationToken.None);
        Assert.False(second.Success, "result replay ignored the caller identity");
    }

    [Fact]
    public async Task ConcurrentDuplicatesMustExecuteExactlyOnceForTheSameAuthorizedCaller()
    {
        // 复核 README L44 口径：断言"**首次成功**"与"**相同 A/operationId 并发执行恰好 1 次**"。
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        dispatcher.SetHandler(handler);

        var command = Command();
        var first = dispatcher.DispatchAsync(command, CancellationToken.None);
        var second = dispatcher.DispatchAsync(command, CancellationToken.None);
        handler.Gate.SetResult(true);
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, r => Assert.True(r.Success, $"authorized duplicates must succeed; error={r.ErrorCode}"));
        Assert.Equal(1, handler.Count);
    }
}
