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

        var result = await route.RouteAsync(Command(BrowserBridgeCommandNames.ContextCreate), CancellationToken.None);

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
    public async Task SnapshotAndLocate_AreRoutedAndProjectedToBridgeDescriptors()
    {
        var snapshotRoute = new BrowserBridgeExecutorRoute((capability, _, _, _) =>
        {
            Assert.Equal(DesktopCapability.BrowserSnapshot, capability.Capability);
            return Task.FromResult(DesktopCapabilityResponse.FromSnapshot(new DesktopSnapshot(
                new DesktopPageTarget("ctx", "page"),
                domText: "<main/>",
                accessibilityTree: null,
                html: null,
                truncated: false,
                nodeCount: 3,
                pageVersion: DesktopPageVersion.Require(4))));
        });

        var snapshot = await snapshotRoute.RouteAsync(
            Command(BrowserBridgeCommandNames.PageSnapshot), CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Success);
        Assert.Equal("<main/>", snapshot.Value!.Value.GetProperty("domText").GetString());
        Assert.Equal(3, snapshot.Value.Value.GetProperty("nodeCount").GetInt32());

        var locateRoute = new BrowserBridgeExecutorRoute((capability, _, _, _) =>
        {
            Assert.Equal(DesktopCapability.BrowserLocate, capability.Capability);
            return Task.FromResult(DesktopCapabilityResponse.FromLocate(new DesktopLocateResult(
                new DesktopPageTarget("ctx", "page"),
                new DesktopLocator(DesktopLocatorKind.Css, "#go"),
                [new DesktopElementRef("v4-n1", "button", DesktopPageVersion.Require(4))],
                truncated: false,
                pageVersion: DesktopPageVersion.Require(4))));
        });

        var locate = await locateRoute.RouteAsync(
            Command(BrowserBridgeCommandNames.PageLocate) with
            {
                Arguments = JsonSerializer.SerializeToElement(
                    new { locator = new { kind = "css", value = "#go" } },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            },
            CancellationToken.None);
        Assert.NotNull(locate);
        Assert.True(locate!.Success);
        Assert.Equal("v4-n1", locate.Value!.Value.GetProperty("elements")[0].GetProperty("ref").GetString());
    }

    [Fact]
    public async Task NavigateAndScreenshot_AreRoutedAndProjected()
    {
        var navigateRoute = new BrowserBridgeExecutorRoute((capability, _, _, _) =>
        {
            Assert.Equal(DesktopCapability.WebViewNavigate, capability.Capability);
            return Task.FromResult(DesktopCapabilityResponse.FromNavigate(new NavigateResult(
                NavigateDisposition.Completed,
                new Uri("https://example.com/done"),
                DesktopPageVersion.Require(9),
                Ok: true,
                Title: "Done")));
        });

        var navigate = await navigateRoute.RouteAsync(
            Command(BrowserBridgeCommandNames.PageGoto) with
            {
                Arguments = JsonSerializer.SerializeToElement(
                    new { url = "https://example.com/done" },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            },
            CancellationToken.None);

        Assert.NotNull(navigate);
        Assert.True(navigate!.Success);
        var navValue = navigate.Value!.Value;
        Assert.True(navValue.GetProperty("ok").GetBoolean());
        Assert.Equal("https://example.com/done", navValue.GetProperty("url").GetString());
        Assert.Equal(9, navValue.GetProperty("page").GetProperty("pageVersion").GetInt64());

        var screenshotRoute = new BrowserBridgeExecutorRoute((capability, _, _, _) =>
        {
            Assert.Equal(DesktopCapability.BrowserScreenshot, capability.Capability);
            return Task.FromResult(DesktopCapabilityResponse.FromScreenshot(new BrowserScreenshotResult(
                new DesktopScreenshotMetadata(
                    "obs-1", "image/png", 3, 10, 10, 10, 10,
                    null, null, null, null, null, null, null, null, null,
                    9, 1, 1, DesktopScreenshotDetail.Low),
                [new DesktopScreenshotChunk(0, true, [1, 2, 3])])));
        });

        var screenshot = await screenshotRoute.RouteAsync(
            Command(BrowserBridgeCommandNames.PageScreenshot) with
            {
                Arguments = JsonSerializer.SerializeToElement(
                    new { expectedPageVersion = 9 },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            },
            CancellationToken.None);

        Assert.NotNull(screenshot);
        Assert.True(screenshot!.Success);
        Assert.Equal("obs-1", screenshot.Value!.Value.GetProperty("observationId").GetString());
        Assert.Equal("AQID", screenshot.Value.Value.GetProperty("chunks")[0].GetProperty("base64").GetString());
    }

    [Fact]
    public async Task TabsCommands_AreRoutedAndProjected()
    {
        var pageState = new DesktopPageState(
            new DesktopPageTarget("ctx", "page-2"),
            new Uri("https://example.com/new"),
            DesktopPageVersion.Require(2),
            DesktopPageReadiness.Loading,
            "New tab");

        var createRoute = new BrowserBridgeExecutorRoute((capability, request, _, _) =>
        {
            Assert.Equal(DesktopCapability.BrowserTabs, capability.Capability);
            Assert.Equal(DesktopTabAction.New, request.Tabs!.Action);
            return Task.FromResult(DesktopCapabilityResponse.FromTabs(new DesktopTabsResult(
                request.Tabs.Target ?? new DesktopPageTarget("ctx", "page-2"),
                DesktopTabAction.New,
                pageState,
                tabClosed: false,
                new DesktopContexts([]))));
        });

        var create = await createRoute.RouteAsync(
            Command(BrowserBridgeCommandNames.PageCreate) with { PageId = null },
            CancellationToken.None);

        Assert.NotNull(create);
        Assert.True(create!.Success);
        Assert.Equal("page-2", create.Value!.Value.GetProperty("pageId").GetString());

        var closeRoute = new BrowserBridgeExecutorRoute((capability, _, _, _) =>
        {
            Assert.Equal(DesktopCapability.BrowserTabs, capability.Capability);
            return Task.FromResult(DesktopCapabilityResponse.FromTabs(new DesktopTabsResult(
                new DesktopPageTarget("ctx", "page"),
                DesktopTabAction.Close,
                pageState,
                tabClosed: true,
                new DesktopContexts([]))));
        });

        var close = await closeRoute.RouteAsync(
            Command(BrowserBridgeCommandNames.PageClose) with
            {
                Arguments = JsonSerializer.SerializeToElement(
                    new { pageId = "page", expectedPageVersion = 2 },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            },
            CancellationToken.None);

        Assert.NotNull(close);
        Assert.True(close!.Success);
        // 与旧 controller 兼容：close 成功无载荷。
        Assert.Null(close.Value);
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
