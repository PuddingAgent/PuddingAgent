using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingBrowser.WebView2;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// Tests for BrowserWorkspaceController as IBrowserCommandHandler:
/// real command execution, pause/takeover gating, context/page lifecycle.
/// Uses fake IBrowserRuntime/Context/Page to avoid real WebView2.
/// </summary>
public class BrowserWorkspaceControllerTests
{
    private static BrowserBridgeCommand MakeCommand(
        string name,
        string? pageId = null,
        string? contextId = null,
        object? args = null)
    {
        return new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = name,
            PageId = pageId,
            ContextId = contextId,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            Arguments = JsonSerializer.SerializeToElement(args ?? new { })
        };
    }

    [Fact]
    public async Task ExecuteAsync_ContextCreate_ReturnsDescriptor()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PageCreate_ReturnsPageDescriptor()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.PageCreate, args: new { initialUrl = "https://example.com", activate = true });
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Value);

            // Verify tab was created
            Assert.Single(controller.Tabs);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PageGoto_ReturnsNavigationResult()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            // Create a page first
            var createCmd = MakeCommand(BrowserBridgeCommandNames.PageCreate);
            var createResult = await controller.ExecuteAsync(createCmd, CancellationToken.None);
            Assert.True(createResult.Success);

            var pageId = controller.ActivePageId!.Value.Value;

            // Navigate
            var gotoCmd = MakeCommand(BrowserBridgeCommandNames.PageGoto,
                pageId: pageId,
                args: new { url = "https://example.com", timeoutMs = 5000 });
            var gotoResult = await controller.ExecuteAsync(gotoCmd, CancellationToken.None);

            Assert.True(gotoResult.Success);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCommand_ReturnsNotSupported()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand("unknown.command");
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserOperationNotSupported, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PageNotFound_ReturnsError()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.PageGetInfo, pageId: "nonexistent");
            var result = await controller.ExecuteAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserPageNotFound, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_WithController_SuccessPath()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

            Assert.True(result.Success);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_Paused_RejectsCommand()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);
        dispatcher.SetPaused(true);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserPaused, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_UserTakeover_RejectsCommand()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);
        dispatcher.SetUserTakeover(true);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, result.ErrorCode);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Dispatcher_DuplicateOperationId_ReturnsCached()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        dispatcher.SetHandler(controller);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var opId = Guid.NewGuid();
            var cmd1 = new BrowserBridgeCommand
            {
                OperationId = opId,
                Name = BrowserBridgeCommandNames.ContextCreate,
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
                Arguments = JsonSerializer.SerializeToElement(new { })
            };

            var result1 = await dispatcher.DispatchAsync(cmd1, CancellationToken.None);
            Assert.True(result1.Success);

            // Same operation id again — should return cached
            var cmd2 = new BrowserBridgeCommand
            {
                OperationId = opId,
                Name = BrowserBridgeCommandNames.ContextCreate,
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
                Arguments = JsonSerializer.SerializeToElement(new { })
            };
            var result2 = await dispatcher.DispatchAsync(cmd2, CancellationToken.None);

            Assert.True(result2.Success);
            Assert.Equal(result1.OperationId, result2.OperationId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TwoPages_ShareContext_DifferentPageIds()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var page1 = await controller.CreatePageAsync(null, true);
            var page2 = await controller.CreatePageAsync(null, false);

            Assert.NotEqual(page1, page2);
            Assert.Equal(2, controller.Tabs.Count);

            // Both share the same context
            Assert.NotNull(controller.ActiveContextId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CloseLastPage_KeepsContext()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        var tempDir = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            var page1 = await controller.CreatePageAsync(null, true);
            Assert.NotNull(controller.ActiveContextId);

            await controller.ClosePageAsync(page1, CancellationToken.None);

            // Context should still exist (persistent)
            Assert.NotNull(controller.ActiveContextId);
            Assert.Empty(controller.Tabs);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TwoPages_CreateTwoSurfaces_AndActivateOnlySelectedPage()
    {
        var surfaceHost = new FakeBrowserSurfaceHost();
        var runtime = new FakeBrowserRuntime(surfaceHost);
        var controller = new BrowserWorkspaceController(runtime, surfaceHost, new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var first = await controller.CreatePageAsync(null, true);
            var second = await controller.CreatePageAsync(null, false);

            Assert.Equal([first, second], surfaceHost.CreatedSurfaces);
            Assert.Equal(first, controller.ActivePageId);
            Assert.Equal(first, surfaceHost.ActivatedSurfaces.Last());

            await controller.ActivateAsync(second, CancellationToken.None);

            Assert.Equal(second, controller.ActivePageId);
            Assert.Equal(second, surfaceHost.ActivatedSurfaces.Last());
            Assert.True(controller.Tabs.Single(tab => tab.PageId == second).IsActive);
            Assert.False(controller.Tabs.Single(tab => tab.PageId == first).IsActive);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AgentTarget_RemainsStableAcrossVisibleTabSwitch_AndDrivesImplicitCommand()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var first = await controller.CreatePageAsync(null, true);
            var second = await controller.CreatePageAsync(null, true);
            await controller.AssignAgentTargetAsync(first, CancellationToken.None);
            await controller.ActivateAsync(second, CancellationToken.None);

            var result = await controller.ExecuteAsync(new BrowserBridgeCommand
            {
                OperationId = Guid.NewGuid(),
                Name = BrowserBridgeCommandNames.PageGoto,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Arguments = JsonSerializer.SerializeToElement(new { url = "https://target.example/" })
            }, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(first, controller.AgentTargetPageId);
            Assert.Equal(second, controller.ActivePageId);
            Assert.Equal("https://target.example/", controller.Tabs.Single(tab => tab.PageId == first).Url);
            Assert.Equal("about:blank", controller.Tabs.Single(tab => tab.PageId == second).Url);
            Assert.True(controller.Tabs.Single(tab => tab.PageId == first).IsAgentTarget);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ClosingAgentTarget_DoesNotFallBackToActivePage()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var target = await controller.CreatePageAsync(null, true);
            await controller.CreatePageAsync(null, true);
            await controller.AssignAgentTargetAsync(target, CancellationToken.None);
            await controller.ClosePageAsync(target, CancellationToken.None);

            var result = await controller.ExecuteAsync(new BrowserBridgeCommand
            {
                OperationId = Guid.NewGuid(),
                Name = BrowserBridgeCommandNames.PageGetInfo,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Arguments = JsonSerializer.SerializeToElement(new { })
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserPageNotFound, result.ErrorCode);
            Assert.Null(controller.AgentTargetPageId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task NavigationOperations_UpdateTabStateThroughUiDispatcher()
    {
        var uiDispatcher = new FakeUiDispatcher();
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), uiDispatcher);
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var page = await controller.CreatePageAsync(null, true);
            await controller.NavigateAsync(page, new Uri("https://example.com/"), CancellationToken.None);

            Assert.True(controller.CanGoBack);
            Assert.False(controller.CanGoForward);

            await controller.GoBackAsync(page, CancellationToken.None);
            Assert.False(controller.CanGoBack);
            Assert.True(controller.CanGoForward);

            await controller.GoForwardAsync(page, CancellationToken.None);
            Assert.True(controller.CanGoBack);
            Assert.False(controller.CanGoForward);
            Assert.True(uiDispatcher.InvocationCount >= 4);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ActivityProjection_UpsertsCompletion_AndKeepsLatestHundred()
    {
        var controller = new BrowserWorkspaceController(
            new FakeBrowserRuntime(), new FakeBrowserSurfaceHost(), new FakeUiDispatcher());

        for (var index = 0; index < 101; index++)
        {
            var operationId = Guid.NewGuid();
            var started = DateTimeOffset.UtcNow.AddSeconds(index);
            await controller.ApplyActivityAsync(new AgentBrowserActivitySnapshot
            {
                OperationId = operationId,
                CommandName = BrowserBridgeCommandNames.PageGoto,
                Target = $"page-{index}",
                StartedAt = started
            }, CancellationToken.None);

            await controller.ApplyActivityAsync(new AgentBrowserActivitySnapshot
            {
                OperationId = operationId,
                CommandName = BrowserBridgeCommandNames.PageGoto,
                Target = $"page-{index}",
                StartedAt = started,
                CompletedAt = started.AddMilliseconds(25),
                Success = true
            }, CancellationToken.None);
        }

        Assert.Equal(100, controller.Activities.Count);
        Assert.All(controller.Activities, activity => Assert.True(activity.IsCompleted));
        Assert.DoesNotContain(controller.Activities, activity => activity.Target == "page-0");
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task Dispatcher_PublishesStartAndCompletion_AndClearHandlerRejectsNewCommands()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var handler = new SuccessfulBrowserHandler();
        dispatcher.SetHandler(handler);
        var snapshots = new List<AgentBrowserActivitySnapshot>();
        dispatcher.ActivityChanged += (_, args) => snapshots.Add(args.Snapshot);
        var command = new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = BrowserBridgeCommandNames.ContextCreate,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            Arguments = JsonSerializer.SerializeToElement(new { })
        };

        var completed = await dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.True(completed.Success);
        Assert.Equal(2, snapshots.Count);
        Assert.False(snapshots[0].IsCompleted);
        Assert.True(snapshots[1].IsCompleted);

        dispatcher.ClearHandler(handler);
        var unavailable = await dispatcher.DispatchAsync(command with { OperationId = Guid.NewGuid() }, CancellationToken.None);
        Assert.False(unavailable.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserNotAvailable, unavailable.ErrorCode);
    }

    [Fact]
    public async Task Dispatcher_DisconnectFailsActiveCommandWithDisconnectedCode()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var handler = new BlockingBrowserHandler();
        dispatcher.SetHandler(handler);
        var command = new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = BrowserBridgeCommandNames.ContextCreate,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            Arguments = JsonSerializer.SerializeToElement(new { })
        };

        var pending = dispatcher.DispatchAsync(command, CancellationToken.None);
        await handler.Started.Task;
        dispatcher.FailAllPending(
            BrowserBridgeErrorCodes.BrowserBridgeDisconnected,
            "Bridge disconnected during command");

        var result = await pending;
        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserBridgeDisconnected, result.ErrorCode);
    }

    [Fact]
    public async Task InitializeFailure_CanRetryWithoutDuplicateContext()
    {
        var runtime = new FakeBrowserRuntime { FailNextContextCreation = true };
        var controller = new BrowserWorkspaceController(runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.InitializeAsync(tempDir, CancellationToken.None));

            await controller.InitializeAsync(tempDir, CancellationToken.None);

            Assert.NotNull(controller.ActiveContextId);
            Assert.Equal(1, runtime.ContextCount);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BridgeContextList_ReturnsInitializedDesktopContext()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var command = new BrowserBridgeCommand
            {
                OperationId = Guid.NewGuid(),
                Name = BrowserBridgeCommandNames.ContextList,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Arguments = JsonSerializer.SerializeToElement(new { })
            };

            var result = await controller.ExecuteAsync(command, CancellationToken.None);

            Assert.True(result.Success);
            var descriptor = result.Value!.Value.Deserialize<BrowserContextListDescriptor>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var context = Assert.Single(descriptor!.Contexts);
            Assert.Equal(controller.ActiveContextId!.Value.Value, context.ContextId);
            Assert.Equal(0, context.PageCount);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BridgePageCreate_AssignsAgentTarget_WhileUserTabSwitchDoesNotChangeIt()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var first = await controller.CreatePageAsync(activate: true, ct: CancellationToken.None);
            var command = new BrowserBridgeCommand
            {
                OperationId = Guid.NewGuid(),
                ContextId = controller.ActiveContextId!.Value.Value,
                Name = BrowserBridgeCommandNames.PageCreate,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Arguments = JsonSerializer.SerializeToElement(new PageCreateArguments { Activate = true })
            };

            var result = await controller.ExecuteAsync(command, CancellationToken.None);
            var created = result.Value!.Value.Deserialize<BrowserPageDescriptor>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await controller.ActivateAsync(first, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(created);
            Assert.Equal(created!.PageId, controller.AgentTargetPageId!.Value.Value);
            Assert.Equal(first, controller.ActivePageId);
            Assert.NotEqual(controller.ActivePageId, controller.AgentTargetPageId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BridgeSnapshotLocateInteractAndWait_DelegateToPageAndReturnTypedResults()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();
        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);
            var pageId = await controller.CreatePageAsync(activate: true, ct: CancellationToken.None);
            var snapshot = await controller.ExecuteAsync(MakeCommand(
                BrowserBridgeCommandNames.PageSnapshot, pageId.Value, args: new PageSnapshotArguments()), CancellationToken.None);
            var locate = await controller.ExecuteAsync(MakeCommand(
                BrowserBridgeCommandNames.PageLocate, pageId.Value, args: new PageLocateArguments
                {
                    Locator = new BrowserLocatorDescriptor { Kind = "role", Value = "button", Name = "Save" }
                }), CancellationToken.None);
            var interact = await controller.ExecuteAsync(MakeCommand(
                BrowserBridgeCommandNames.PageInteract, pageId.Value, args: new PageInteractArguments
                {
                    Action = "click",
                    Locator = new BrowserLocatorDescriptor { Kind = "ref", Value = "v0-n1" }
                }), CancellationToken.None);
            var wait = await controller.ExecuteAsync(MakeCommand(
                BrowserBridgeCommandNames.PageWaitFor, pageId.Value, args: new PageWaitForArguments
                {
                    Selector = "#saved",
                    TimeoutMs = 1000
                }), CancellationToken.None);

            Assert.True(snapshot.Success);
            Assert.True(locate.Success, $"{locate.ErrorCode}: {locate.ErrorMessage}");
            Assert.True(interact.Success, $"{interact.ErrorCode}: {interact.ErrorMessage}");
            Assert.True(wait.Success, $"{wait.ErrorCode}: {wait.ErrorMessage}");
            Assert.Contains("v0-n1", locate.Value!.Value.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentBrowserUdf_DiffersFrom_WorkbenchUdf()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        var agentUdf = Path.GetFullPath(Path.Combine(dataRoot, "browser", "agent-browser", "user-data"));
        var workbenchUdf = Path.GetFullPath(Path.Combine(dataRoot, "browser", "workbench", "user-data"));

        Assert.NotEqual(agentUdf, workbenchUdf);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

// ─── Fake implementations for testing without real WebView2 ──────────────────

internal sealed class FakeBrowserRuntime : IBrowserRuntime
{
    private readonly Dictionary<BrowserContextId, FakeBrowserContext> _contexts = new();
    private readonly FakeBrowserSurfaceHost? _surfaceHost;

    public bool FailNextContextCreation { get; set; }
    public int ContextCount => _contexts.Count;

    public BrowserRuntimeState State { get; private set; } = BrowserRuntimeState.Created;

    public FakeBrowserRuntime(FakeBrowserSurfaceHost? surfaceHost = null)
        => _surfaceHost = surfaceHost;

    public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct)
    {
        if (FailNextContextCreation)
        {
            FailNextContextCreation = false;
            throw new InvalidOperationException("Scripted context creation failure");
        }
        var id = options.Id ?? new BrowserContextId(Guid.NewGuid().ToString("N"));
        var context = new FakeBrowserContext(id, options, _surfaceHost);
        _contexts[id] = context;
        State = BrowserRuntimeState.Ready;
        return Task.FromResult<IBrowserContext>(context);
    }

    public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
    {
        _contexts.TryGetValue(id, out var ctx);
        return Task.FromResult<IBrowserContext?>(ctx);
    }

    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BrowserContextInfo>>(_contexts.Values.Select(c => c.Info).ToList());

    public Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
    {
        _contexts.Remove(id);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        State = BrowserRuntimeState.Disposed;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrowserContext : IBrowserContext
{
    private readonly Dictionary<PageId, FakeBrowserPage> _pages = new();

    public BrowserContextId Id { get; }
    public BrowserContextInfo Info { get; private set; }

    private readonly FakeBrowserSurfaceHost? _surfaceHost;

    public FakeBrowserContext(
        BrowserContextId id,
        BrowserContextOptions options,
        FakeBrowserSurfaceHost? surfaceHost = null)
    {
        _surfaceHost = surfaceHost;
        Id = id;
        Info = new BrowserContextInfo
        {
            Id = id,
            UserDataDirectory = options.UserDataDirectory ?? Path.GetTempPath(),
            Persistent = options.Persistent,
            PageCount = 0
        };
    }

    public async Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct)
    {
        var pageId = new PageId(Guid.NewGuid().ToString("N"));
        if (_surfaceHost is not null)
            await _surfaceHost.CreateAsync(Id, pageId, null!, options, ct);
        var page = new FakeBrowserPage(pageId, Id);
        _pages[pageId] = page;
        Info = Info with { PageCount = _pages.Count };
        return page;
    }

    public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct)
    {
        _pages.TryGetValue(id, out var page);
        return Task.FromResult<IBrowserPage?>(page);
    }

    public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PageInfo>>(_pages.Values.Select(p => p.Info).ToList());

    public Task ClosePageAsync(PageId id, CancellationToken ct)
    {
        _pages.Remove(id);
        Info = Info with { PageCount = _pages.Count };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => Task.CompletedTask;
    public Task ClearCookiesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => Task.CompletedTask;
    public Task ResetPermissionsAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _pages.Clear();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrowserPage : IBrowserPage
{
    private long _version;

    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public long PageVersion => Interlocked.Read(ref _version);
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }
    public bool IsLoading { get; private set; }
    public PageInfo Info { get; private set; }

    public FakeBrowserPage(PageId id, BrowserContextId contextId)
    {
        Id = id;
        ContextId = contextId;
        Info = new PageInfo { Id = id, ContextId = contextId, Title = "Fake", Url = "about:blank", PageVersion = 0 };
    }

    public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct)
    {
        Interlocked.Increment(ref _version);
        Info = Info with { Url = url.ToString(), PageVersion = PageVersion };
        CanGoBack = true;
        CanGoForward = false;
        IsLoading = false;
        return Task.FromResult(new NavigationResult { Url = url, Ok = true, StatusCode = 200 });
    }

    public Task GoBackAsync(CancellationToken ct)
    {
        CanGoBack = false;
        CanGoForward = true;
        return Task.CompletedTask;
    }
    public Task GoForwardAsync(CancellationToken ct)
    {
        CanGoBack = true;
        CanGoForward = false;
        return Task.CompletedTask;
    }
    public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task BringToFrontAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
        => Task.FromResult(new PageSnapshot
        {
            AccessibilityTree = $"button ref=v{PageVersion}-n1 name=\"Save\"",
            NodeCount = 1
        });
    public async Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        => (await QueryAllAsync(locator, ct)).FirstOrDefault();
    public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
    {
        if (locator.Kind == LocatorKind.Ref && locator.Value != $"v{PageVersion}-n1")
            throw new BrowserOperationException("stale_element_reference", "stale ref");
        return Task.FromResult<IReadOnlyList<IElementHandle>>(
        [
            new ControllerFakeElementHandle(Id, PageVersion, new BrowserElementInfo
            {
                Ref = $"v{PageVersion}-n1", Tag = "button", Role = "button", Name = "Save",
                Text = "Save", Visible = true, Enabled = true
            })
        ]);
    }
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => throw new NotSupportedException();
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => throw new NotSupportedException();
    public Task UnsubscribeAsync(BrowserSubscriptionId sid, CancellationToken ct) => throw new NotSupportedException();
    public Task ClickAsync(Locator l, ClickOptions o, CancellationToken ct) => Task.CompletedTask;
    public Task FillAsync(Locator l, string v, FillOptions o, CancellationToken ct) => Task.CompletedTask;
    public Task TypeAsync(Locator l, string t, TypeOptions o, CancellationToken ct) => Task.CompletedTask;
    public Task PressAsync(Locator l, string k, KeyOptions o, CancellationToken ct) => Task.CompletedTask;
    public Task HoverAsync(Locator l, PointerOptions o, CancellationToken ct) => Task.CompletedTask;
    public Task ScrollAsync(ScrollOptions o, CancellationToken ct) => Task.CompletedTask;
    public Task DragAsync(Locator s, Locator t, DragOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task SelectAsync(Locator l, IReadOnlyList<string> v, CancellationToken ct) => Task.CompletedTask;
    public Task CheckAsync(Locator l, bool c, CancellationToken ct) => Task.CompletedTask;
    public Task SetInputFilesAsync(Locator l, IReadOnlyList<string> p, CancellationToken ct) => throw new NotSupportedException();
    public Task<WaitResult> WaitForAsync(WaitCondition c, CancellationToken ct)
        => Task.FromResult(new WaitResult { TimedOut = false });
    public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task<PdfResult> PrintToPdfAsync(PdfOptions o, CancellationToken ct) => throw new NotSupportedException();
    public Task OpenDevToolsAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ControllerFakeElementHandle : IElementHandle
{
    public ControllerFakeElementHandle(PageId pageId, long pageVersion, BrowserElementInfo info)
    {
        PageId = pageId;
        PageVersion = pageVersion;
        Info = info;
        Id = new ElementHandleId(info.Ref);
    }
    public ElementHandleId Id { get; }
    public PageId PageId { get; }
    public long PageVersion { get; }
    public int? BackendNodeId => null;
    public string LocatorFingerprint => "controller-fake";
    public BrowserElementInfo Info { get; }
    public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct) => Task.FromResult(Info.BoundingBox);
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}


// ─── Fake SurfaceHost and UiDispatcher for Controller tests ─────────────────

internal sealed class FakeBrowserSurfaceHost : PuddingBrowser.WebView2.IBrowserSurfaceHost
{
    public List<PageId> CreatedSurfaces { get; } = new();
    public List<PageId> ActivatedSurfaces { get; } = new();
    public List<PageId> ClosedSurfaces { get; } = new();

    public Task<PuddingBrowser.WebView2.IBrowserSurface> CreateAsync(
        BrowserContextId contextId, PageId pageId,
        Microsoft.Web.WebView2.Core.CoreWebView2Environment environment,
        PageCreateOptions options, CancellationToken ct)
    {
        CreatedSurfaces.Add(pageId);
        return Task.FromResult<PuddingBrowser.WebView2.IBrowserSurface>(new FakeBrowserSurface(pageId));
    }

    public Task ActivateAsync(PageId pageId, CancellationToken ct)
    {
        ActivatedSurfaces.Add(pageId);
        return Task.CompletedTask;
    }

    public Task CloseAsync(PageId pageId, CancellationToken ct)
    {
        ClosedSurfaces.Add(pageId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeBrowserSurface : PuddingBrowser.WebView2.IBrowserSurface
{
    public FakeBrowserSurface(PageId pageId) => PageId = pageId;
    public PageId PageId { get; }
    public Microsoft.Web.WebView2.Wpf.WebView2CompositionControl Control => throw new NotSupportedException();
    public Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeUiDispatcher : PuddingBrowser.WebView2.IWebView2UiDispatcher
{
    private int _invocationCount;
    public int InvocationCount => Volatile.Read(ref _invocationCount);
    public Task InvokeAsync(Func<Task> action, CancellationToken ct)
    {
        Interlocked.Increment(ref _invocationCount);
        return action();
    }
    public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        Interlocked.Increment(ref _invocationCount);
        return action();
    }
}

internal sealed class SuccessfulBrowserHandler : IBrowserCommandHandler
{
    public Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        => Task.FromResult(new BrowserBridgeCommandResult
        {
            OperationId = command.OperationId,
            Success = true,
            Value = JsonSerializer.SerializeToElement(new { ok = true })
        });
}

internal sealed class BlockingBrowserHandler : IBrowserCommandHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("Unreachable");
    }
}
