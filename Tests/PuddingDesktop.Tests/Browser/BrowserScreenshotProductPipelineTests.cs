using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// **F1 的生产组合根集成证据**（方案 §6 第 2 级）：能力通道关闭时，默认 Bridge 截图必须
/// 经工作区执行器到达真实表面（假 driver 只替身 WebView2，不替身执行器/准入/分发链）。
///
/// 组装：真实 <see cref="BrowserPageLifecycle"/> + <see cref="DesktopSurfaceComposition"/> +
/// <see cref="DesktopBrowserExecutorComposition"/> + <see cref="BrowserWorkspaceController"/> +
/// <see cref="BrowserBridgeCommandDispatcher"/>；能力通道**从未构造**。
/// </summary>
public sealed class BrowserScreenshotProductPipelineTests
{
    [Fact]
    public async Task ScreenshotOverTheDefaultBridge_ReachesTheDriverExactlyOnceWithoutACapabilityChannel()
    {
        var harness = await Harness.CreateAsync();

        try
        {
            var command = harness.ScreenshotCommand(harness.PageVersion);
            var result = await harness.Dispatcher.DispatchAsync(command, CancellationToken.None);

            Assert.True(result.Success, $"screenshot failed: {result.ErrorCode}: {result.ErrorMessage}");

            // 驱动真的被调用了一次（分发链走的是执行器 → 表面 → 页面，而不是 controller 直接截）。
            Assert.Equal(1, harness.Page.CaptureScreenshotCount);
            Assert.Equal(1, harness.Page.LayoutMetricsCount);
            Assert.Equal(1, harness.Page.RuntimeEvaluateCount);

            var value = result.Value!.Value;
            Assert.False(string.IsNullOrEmpty(value.GetProperty("observationId").GetString()));
            Assert.True(value.GetProperty("chunks").GetArrayLength() > 0, "screenshot must carry image bytes");
            Assert.True(value.GetProperty("byteCount").GetInt64() > 0);
            Assert.Equal(harness.PageVersion, value.GetProperty("pageVersion").GetInt64());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ScreenshotWithAStaleVersion_IsRejectedBeforeTheDriverIsTouched()
    {
        var harness = await Harness.CreateAsync();

        try
        {
            var stale = harness.ScreenshotCommand(harness.PageVersion + 1);
            var result = await harness.Dispatcher.DispatchAsync(stale, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(0, harness.Page.CaptureScreenshotCount);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ScreenshotWithNoHandedOverPage_IsRejectedBeforeTheDriverIsTouched()
    {
        var harness = await Harness.CreateAsync(handOver: false);

        try
        {
            var result = await harness.Dispatcher.DispatchAsync(
                harness.ScreenshotCommand(harness.PageVersion),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("agent_target_required", result.ErrorCode);
            Assert.Equal(0, harness.Page.CaptureScreenshotCount);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly BrowserPageLifecycle _lifecycle;
        private readonly IDesktopBrowserExecutor _executor;
        private readonly BrowserWorkspaceController _controller;
        private readonly string _dataRoot;

        private Harness(
            BrowserPageLifecycle lifecycle,
            IDesktopBrowserExecutor executor,
            BrowserWorkspaceController controller,
            BrowserBridgeCommandDispatcher dispatcher,
            FakeBrowserPage page,
            string dataRoot,
            string contextId,
            long pageVersion)
        {
            _lifecycle = lifecycle;
            _executor = executor;
            _controller = controller;
            Dispatcher = dispatcher;
            Page = page;
            _dataRoot = dataRoot;
            ContextId = contextId;
            PageVersion = pageVersion;
        }

        public BrowserBridgeCommandDispatcher Dispatcher { get; }
        public FakeBrowserPage Page { get; }
        public string ContextId { get; }
        public long PageVersion { get; }

        public static async Task<Harness> CreateAsync(bool handOver = true)
        {
            var lifecycle = BrowserTestState.CreateLifecycle();
            lifecycle.NotifyBridgeConnected(BrowserTestState.Generation.Value);

            var runtime = new FakeBrowserRuntime();
            var surfaceHost = new FakeBrowserSurfaceHost();
            var uiDispatcher = new FakeUiDispatcher();
            var surface = new DesktopSurfaceComposition(
                runtime,
                lifecycle.BrowserTargets,
                lifecycle.Readings,
                new DesktopShellSurface(new FakeShellFacilities(), new FakeShellHostFacilities()));
            var executor = DesktopBrowserExecutorComposition.Create(
                new InlineDesktopUiDispatcher(),
                surface,
                lifecycle,
                options: new DesktopServiceOptions
                {
                    AllowedCapabilities = DesktopCapability.BrowserScreenshot,
                    BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
                });

            var controller = new BrowserWorkspaceController(
                runtime,
                surfaceHost,
                uiDispatcher,
                readings: lifecycle.Readings,
                capabilityExecutor: () => executor);

            var dataRoot = Path.Combine(Path.GetTempPath(), $"pwshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataRoot);

            try
            {
                await controller.InitializeAsync(dataRoot, CancellationToken.None);
                var pageId = await controller.CreatePageAsync("https://example.com/", activate: true);
                await controller.NavigateAsync(pageId, new Uri("https://example.com/"), CancellationToken.None);
                var page = runtime.Pages.Single();
                var contextId = controller.ActiveContextId!.Value.Value;
                var version = page.PageVersion;
                Assert.True(version > 0, "page must have a real version after navigation");

                lifecycle.RegisterContext(contextId, DesktopContextTrust.AgentAuthorized);
                lifecycle.RegisterPage(
                    new DesktopPageTarget(contextId, pageId.Value),
                    DesktopPageVersion.Require(version),
                    DesktopPageReadiness.Interactive);

                if (handOver)
                {
                    lifecycle.SetAgentTarget(contextId, pageId.Value);
                }

                var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
                dispatcher.SetHandler(controller);

                return new Harness(lifecycle, executor, controller, dispatcher, page, dataRoot, contextId, version);
            }
            catch
            {
                await controller.DisposeAsync();
                await executor.DisposeAsync();
                throw;
            }
        }

        public BrowserBridgeCommand ScreenshotCommand(long expectedPageVersion) => new()
        {
            OperationId = Guid.NewGuid(),
            Name = BrowserBridgeCommandNames.PageScreenshot,
            ContextId = ContextId,
            PageId = Page.Id.Value,
            Origin = BrowserTestState.Origin(),
            Caller = new BrowserBridgeCallerIdentity
            {
                Kind = "agent",
                CallerId = "agent-a",
                TaskId = "run-agent-a",
            },
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            Arguments = JsonSerializer.SerializeToElement(new PageScreenshotArguments
            {
                ExpectedPageVersion = expectedPageVersion,
                Format = "png",
            }),
        };

        public async ValueTask DisposeAsync()
        {
            await _controller.DisposeAsync();
            await _executor.DisposeAsync();

            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // temp 清理失败不影响测试结论。
            }
        }
    }

    private sealed class InlineDesktopUiDispatcher : IDesktopUiDispatcher
    {
        public bool HasThreadAccess => true;

        public Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
            => action(cancellationToken);

        public Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
            => action(cancellationToken);
    }

    private sealed class FakeShellFacilities : IDesktopShellFacilities
    {
        private static CapabilityResult<T> NotScripted<T>() =>
            CapabilityResult<T>.Failure(DesktopCapabilityError.UnsupportedCapability("not scripted in this test"));

        public Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
            DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken)
            => Task.FromResult(NotScripted<DesktopDialogResult>());

        public Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
            DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(NotScripted<DesktopFilePickerResult>());

        public Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
            DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken)
            => Task.FromResult(NotScripted<DesktopClipboardContent>());
    }

    private sealed class FakeShellHostFacilities : IDesktopShellHostFacilities
    {
        public Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
            DesktopCallContext context, CancellationToken cancellationToken)
            => Task.FromResult(CapabilityResult<DesktopShellStatus>.Success(new DesktopShellStatus(
                DesktopWindowState.HiddenToTray,
                trayVisible: true,
                DesktopAutomationState.Free,
                openPageCount: 0)));

        public Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
            DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(CapabilityResult<DesktopNotificationResult>.Success(
                new DesktopNotificationResult(true, "notification-1")));
    }
}
