using System.Text.Json;
using Pudding.Contracts;
using Pudding.DesktopService;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// ADR-097 D3 的 UI 状态契约：交页后必须区分「等待会话确认 / 已交出 / 失效」，
/// 不能因为用户点了按钮就显示 Agent 已取得控制。
/// </summary>
public sealed class BrowserControlSessionUiTests
{
    [Fact]
    public async Task AgentTargetSummary_DistinguishesPendingConfirmedAndCleared()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = Path.Combine(Path.GetTempPath(), $"pwui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var pageId = await controller.CreatePageAsync(activate: true);
            await controller.AssignAgentTargetAsync(pageId, CancellationToken.None);
            var target = $"{controller.ActiveContextId!.Value.Value}/{pageId.Value}";

            // 还没有可信会话确认：不能显示"已交出"。
            Assert.Contains("not yet confirmed", controller.AgentTargetSummary, StringComparison.Ordinal);

            await controller.ApplySessionStatusAsync(
                new BrowserSessionStatus(
                    IsBridgeReady: true,
                    HasTarget: false,
                    HasPendingHandover: true,
                    Target: null,
                    PendingTarget: target,
                    IsPaused: false,
                    IsUserControlling: false,
                    IsClosed: false,
                    Reason: "user handed the page to the current core session"),
                CancellationToken.None);

            Assert.Contains("waiting for session confirmation", controller.AgentTargetSummary, StringComparison.Ordinal);
            Assert.True(controller.ControlSession!.HasPendingHandover);

            await controller.ApplySessionStatusAsync(
                new BrowserSessionStatus(
                    IsBridgeReady: true,
                    HasTarget: true,
                    HasPendingHandover: false,
                    Target: target,
                    PendingTarget: null,
                    IsPaused: false,
                    IsUserControlling: false,
                    IsClosed: false,
                    Reason: "handover claimed by the trusted control owner"),
                CancellationToken.None);

            Assert.Contains("session confirmed", controller.AgentTargetSummary, StringComparison.Ordinal);

            await controller.ApplySessionStatusAsync(
                new BrowserSessionStatus(
                    IsBridgeReady: true,
                    HasTarget: false,
                    HasPendingHandover: false,
                    Target: null,
                    PendingTarget: null,
                    IsPaused: false,
                    IsUserControlling: false,
                    IsClosed: false,
                    Reason: "revoked by user"),
                CancellationToken.None);

            Assert.Contains("not yet confirmed", controller.AgentTargetSummary, StringComparison.Ordinal);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispatcher_PublishesHandoverPendingThenConfirmed()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);

        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        dispatcher.SetHandler(new SuccessfulBrowserHandler());

        var statuses = new List<BrowserSessionStatus>();
        dispatcher.SessionChanged += (_, e) => statuses.Add(e.Status);

        // 用户重复点"交给当前会话"（幂等）⇒ 仍应显示待确认而不是已交出。
        dispatcher.SetAgentTarget("ctx", "page");
        Assert.Contains(statuses, status => status.HasPendingHandover && !status.HasTarget);

        // 第一条可信命令确认归属 ⇒ UI 才应切到"已交出"。
        var command = new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = BrowserBridgeCommandNames.PageGoto,
            ContextId = "ctx",
            PageId = "page",
            Origin = BrowserTestState.Origin(),
            Caller = new BrowserBridgeCallerIdentity
            {
                Kind = "agent",
                CallerId = "agent-a",
                TaskId = "run-agent-a",
            },
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
        };

        var result = await dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.True(result.Success, $"error={result.ErrorCode}: {result.ErrorMessage}");
        Assert.Contains(statuses, status => status.HasTarget && !status.HasPendingHandover);
    }
}
