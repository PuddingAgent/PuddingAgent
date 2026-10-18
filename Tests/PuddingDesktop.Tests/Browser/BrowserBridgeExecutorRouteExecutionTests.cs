using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// I2-c 真实执行器路由：命令→能力请求→执行器调用→Bridge 结果。
/// 用替身 invoker 断言"带会话票据标记/可信调用者"与失败码映射，不需要 WinUI。
/// </summary>
public sealed class BrowserBridgeExecutorRouteExecutionTests
{
    private static BrowserBridgeCommand Command(string name = BrowserBridgeCommandNames.PageGetInfo) => new()
    {
        OperationId = Guid.NewGuid(),
        ContextId = "ctx",
        PageId = "page",
        Origin = BrowserTestState.Origin(),
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "session-agent-a", TaskId = "run-a" },
        Name = name,
        DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        Arguments = JsonSerializer.SerializeToElement(new { }),
    };

    [Fact]
    public async Task PageGetInfo_RoutesThroughTheExecutorWithSessionPermitAndCaller()
    {
        DesktopCallContext? observedContext = null;
        var route = new BrowserBridgeExecutorRoute((capability, request, context, _) =>
        {
            observedContext = context;
            Assert.Equal(DesktopCapability.WebViewPageState, capability.Capability);
            return Task.FromResult(DesktopCapabilityResponse.FromPageState(new DesktopPageState(
                new DesktopPageTarget("ctx", "page"),
                new Uri("https://example.com/a"),
                DesktopPageVersion.Require(7),
                DesktopPageReadiness.Complete,
                "Example")));
        });

        var result = await route.RouteAsync(Command(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.NotNull(observedContext);
        Assert.Equal("bridge-session", observedContext!.SessionPermitMarker);
        Assert.NotNull(observedContext.BrowserCaller);
        Assert.Equal(BrowserCallerKind.Agent, observedContext.BrowserCaller!.Kind);
        Assert.Equal("session-agent-a", observedContext.BrowserCaller.CallerId);

        var page = result.Value!.Value;
        Assert.Equal("page", page.GetProperty("pageId").GetString());
        Assert.Equal("Example", page.GetProperty("title").GetString());
        Assert.Equal(7, page.GetProperty("pageVersion").GetInt64());
    }

    [Fact]
    public async Task UncoveredCommands_ReturnNullSoTheDispatcherFallsBackToTheHandler()
    {
        var route = new BrowserBridgeExecutorRoute((_, _, _, _) =>
            throw new InvalidOperationException("must not be called"));

        var result = await route.RouteAsync(Command(BrowserBridgeCommandNames.PageGoto), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecutorFailures_AreMappedToStableBridgeErrorCodes()
    {
        var route = new BrowserBridgeExecutorRoute((_, _, _, _) =>
            Task.FromResult(DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.Unauthorized("not the agent target"))));

        var result = await route.RouteAsync(Command(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUnauthorized, result.ErrorCode);
    }

    [Fact]
    public async Task MissingCallerIdentity_IsRejectedBeforeTouchingTheExecutor()
    {
        var route = new BrowserBridgeExecutorRoute((_, _, _, _) =>
            throw new InvalidOperationException("must not be called"));
        var command = Command() with { Caller = null };

        var result = await route.RouteAsync(command, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUnauthorized, result.ErrorCode);
    }
}
