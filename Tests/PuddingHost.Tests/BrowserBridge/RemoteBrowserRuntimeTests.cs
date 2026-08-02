using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class RemoteBrowserRuntimeTests
{
    [Fact]
    public async Task CreateContextAsync_MapsDescriptorToRemoteContext()
    {
        var broker = new RecordingBrowserBroker(command => Success(command, new BrowserContextDescriptor
        {
            ContextId = "ctx-1",
            UserDataDirectory = "C:/temp/browser",
            PageCount = 2
        }));
        await using var runtime = new RemoteBrowserRuntime(broker);

        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-requested") },
            CancellationToken.None);

        Assert.Equal("ctx-1", context.Id.Value);
        Assert.Equal(2, context.Info.PageCount);
        var command = Assert.Single(broker.Commands);
        Assert.Equal(BrowserBridgeCommandNames.ContextCreate, command.Name);
        Assert.Equal("ctx-requested", command.ContextId);
    }

    [Fact]
    public async Task ListContextsAsync_UsesDesktopAsSourceOfTruth()
    {
        var broker = new RecordingBrowserBroker(command => Success(command, new BrowserContextListDescriptor
        {
            Contexts =
            [
                new BrowserContextDescriptor
                {
                    ContextId = "ctx-live",
                    UserDataDirectory = "C:/live",
                    PageCount = 3
                }
            ]
        }));
        await using var runtime = new RemoteBrowserRuntime(broker);

        var contexts = await runtime.ListContextsAsync(CancellationToken.None);

        var context = Assert.Single(contexts);
        Assert.Equal("ctx-live", context.Id.Value);
        Assert.Equal(3, context.PageCount);
        Assert.Equal(BrowserBridgeCommandNames.ContextList, Assert.Single(broker.Commands).Name);
    }

    [Fact]
    public async Task RemoteContextAndPage_MapCreateGotoAndListCommands()
    {
        var broker = new RecordingBrowserBroker(command => command.Name switch
        {
            BrowserBridgeCommandNames.ContextCreate => Success(command, Context("ctx-1", 0)),
            BrowserBridgeCommandNames.PageCreate => Success(command, Page("ctx-1", "page-1", "about:blank")),
            BrowserBridgeCommandNames.PageGoto => Success(command, new BrowserNavigationResultDescriptor
            {
                Url = "https://example.com/",
                Ok = true,
                StatusCode = 200,
                Page = Page("ctx-1", "page-1", "https://example.com/") with
                {
                    CanGoBack = true,
                    PageVersion = 2
                }
            }),
            BrowserBridgeCommandNames.PageList => Success(command, new BrowserPageListDescriptor
            {
                Pages = [Page("ctx-1", "page-1", "https://example.com/")]
            }),
            _ => Failure(command, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
        });
        await using var runtime = new RemoteBrowserRuntime(broker);
        var context = await runtime.CreateContextAsync(new BrowserContextOptions(), CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var navigation = await page.GotoAsync(
            new Uri("https://example.com"),
            new NavigationOptions(),
            CancellationToken.None);
        var pages = await context.ListPagesAsync(CancellationToken.None);

        Assert.True(navigation.Ok);
        Assert.Equal(200, navigation.StatusCode);
        Assert.True(page.CanGoBack);
        Assert.Equal(2, page.PageVersion);
        Assert.Single(pages);
        Assert.Equal(
            [
                BrowserBridgeCommandNames.ContextCreate,
                BrowserBridgeCommandNames.PageCreate,
                BrowserBridgeCommandNames.PageGoto,
                BrowserBridgeCommandNames.PageList
            ],
            broker.Commands.Select(command => command.Name));
    }

    [Fact]
    public async Task GetContextAsync_ReturnsNullForStableNotFoundCode()
    {
        var broker = new RecordingBrowserBroker(command =>
            Failure(command, BrowserBridgeErrorCodes.BrowserContextNotFound));
        await using var runtime = new RemoteBrowserRuntime(broker);

        var context = await runtime.GetContextAsync(
            new BrowserContextId("missing"), CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task BrokerFailure_PropagatesStableBrowserOperationException()
    {
        var broker = new RecordingBrowserBroker(command =>
            Failure(command, BrowserBridgeErrorCodes.BrowserNotAvailable));
        await using var runtime = new RemoteBrowserRuntime(broker);

        var exception = await Assert.ThrowsAsync<BrowserOperationException>(
            () => runtime.ListContextsAsync(CancellationToken.None));

        Assert.Equal(BrowserBridgeErrorCodes.BrowserNotAvailable, exception.Code);
    }

    [Fact]
    public async Task Dispose_DoesNotCloseDesktopOwnedBrowserState()
    {
        var broker = new RecordingBrowserBroker(command => Success(command, new { }));
        var runtime = new RemoteBrowserRuntime(broker);

        await runtime.DisposeAsync();

        Assert.Empty(broker.Commands);
        Assert.Equal(BrowserRuntimeState.Disposed, runtime.State);
    }

    [Fact]
    public async Task RemotePage_MapsSnapshotLocateInteractAndWaitCommands()
    {
        var pageDescriptor = Page("ctx-1", "page-1", "https://test.local/") with { PageVersion = 3 };
        var broker = new RecordingBrowserBroker(command => command.Name switch
        {
            BrowserBridgeCommandNames.ContextCreate => Success(command, Context("ctx-1", 0)),
            BrowserBridgeCommandNames.PageCreate => Success(command, pageDescriptor),
            BrowserBridgeCommandNames.PageSnapshot => Success(command, new BrowserSnapshotDescriptor
            {
                AccessibilityTree = "button ref=v3-n1 name=\"Save\"",
                NodeCount = 1,
                PageVersion = 3
            }),
            BrowserBridgeCommandNames.PageLocate => Success(command, new BrowserLocateResultDescriptor
            {
                Elements =
                [
                    new BrowserElementDescriptor
                    {
                        Ref = "v3-n1", Tag = "button", Role = "button", Name = "Save",
                        Visible = true, Enabled = true, PageVersion = 3
                    }
                ]
            }),
            BrowserBridgeCommandNames.PageInteract => Success(command, new BrowserInteractionResultDescriptor
            {
                Element = null,
                Page = pageDescriptor
            }),
            BrowserBridgeCommandNames.PageWaitFor => Success(command, new BrowserWaitResultDescriptor
            {
                TimedOut = false,
                Page = pageDescriptor
            }),
            _ => Failure(command, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
        });
        await using var runtime = new RemoteBrowserRuntime(broker);
        var context = await runtime.CreateContextAsync(new BrowserContextOptions(), CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await page.SnapshotAsync(new SnapshotOptions(), CancellationToken.None);
        var handles = await page.QueryAllAsync(new Locator
        {
            Kind = LocatorKind.Role,
            Value = "button",
            Name = "Save"
        }, CancellationToken.None);
        await page.ClickAsync(new Locator { Kind = LocatorKind.Ref, Value = "v3-n1" }, new ClickOptions(), CancellationToken.None);
        var wait = await page.WaitForAsync(new WaitCondition { Selector = "#saved" }, CancellationToken.None);

        Assert.Equal(1, snapshot.NodeCount);
        Assert.Equal("v3-n1", Assert.Single(handles).Info.Ref);
        Assert.False(wait.TimedOut);
        Assert.Equal(
            [BrowserBridgeCommandNames.ContextCreate, BrowserBridgeCommandNames.PageCreate,
                BrowserBridgeCommandNames.PageSnapshot, BrowserBridgeCommandNames.PageLocate,
                BrowserBridgeCommandNames.PageInteract, BrowserBridgeCommandNames.PageWaitFor],
            broker.Commands.Select(command => command.Name));
    }

    private static BrowserContextDescriptor Context(string contextId, int pageCount) => new()
    {
        ContextId = contextId,
        UserDataDirectory = "C:/browser",
        PageCount = pageCount
    };

    private static BrowserPageDescriptor Page(string contextId, string pageId, string url) => new()
    {
        ContextId = contextId,
        PageId = pageId,
        Title = "Test",
        Url = url,
        PageVersion = 1
    };

    private static BrowserBridgeCommandResult Success(BrowserBridgeCommand command, object value) => new()
    {
        OperationId = command.OperationId,
        Success = true,
        Value = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private static BrowserBridgeCommandResult Failure(BrowserBridgeCommand command, string code) => new()
    {
        OperationId = command.OperationId,
        Success = false,
        ErrorCode = code,
        ErrorMessage = "scripted failure"
    };

    private sealed class RecordingBrowserBroker(
        Func<BrowserBridgeCommand, BrowserBridgeCommandResult> responder)
        : IDesktopBrowserCommandBroker
    {
        public List<BrowserBridgeCommand> Commands { get; } = [];
        public bool IsDesktopConnected => true;

        public Task<BrowserBridgeCommandResult> ExecuteAsync(
            BrowserBridgeCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(responder(command));
        }

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;
        public void HandleResult(Guid connectionId, long generation, BrowserBridgeCommandResult result) { }
        public void FailPendingForConnection(
            Guid connectionId, long generation, string errorCode, string message) { }
    }
}
