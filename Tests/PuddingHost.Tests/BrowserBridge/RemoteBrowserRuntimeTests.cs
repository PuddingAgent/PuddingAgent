using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingBrowser.AgentTools;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class RemoteBrowserRuntimeTests : IDisposable
{
    /// <summary>
    /// 每个用例默认带一个**可信 Agent 身份**：Bridge 命令现在必须有调用者身份（方案 §3.2）。
    /// 需要验证"缺身份被拒"的用例请显式构造不带身份的运行时（并用作用域把身份清掉）。
    /// </summary>
    private readonly IDisposable _callerScope = BrowserCallerIdentityScope.Push(
        new Pudding.Contracts.Desktop.BrowserCallerIdentity(
            Pudding.Contracts.Desktop.BrowserCallerKind.Agent, "agent-test", "run-test"),
        null);

    private readonly PuddingBrowser.AgentTools.BrowserCallerIdentityAccessor _callerAccessor = new();

    public void Dispose() => _callerScope.Dispose();

    [Fact]
    public async Task RemoteRefQuery_SerializesVersionAcceptedByRealDesktopAdapter()
    {
        var broker = new RecordingBrowserBroker(command =>
        {
            if (command.Name == BrowserBridgeCommandNames.ContextCreate)
                return Success(command, Context("ctx-1", 1));
            if (command.Name == BrowserBridgeCommandNames.PageCreate)
                return Success(command, Page("ctx-1", "page-1", "about:blank") with { PageVersion = 7 });
            Assert.Equal(BrowserBridgeCommandNames.PageLocate, command.Name);
            Assert.True(PuddingDesktop.Browser.BrowserBridgeCommandAdapter.TryMapReadCommand(
                command, out _, out var mapped, out var reason), reason);
            Assert.Equal(7, mapped!.Locate!.ExpectedPageVersion.Value);
            Assert.Equal("p1-v7-n3", mapped.Locate.Locator.Value);
            return Success(command, new BrowserLocateResultDescriptor
            {
                Elements = [new BrowserElementDescriptor { Ref = "p1-v7-n3", Tag = "button", PageVersion = 7 }]
            });
        });
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);
        var context = await runtime.CreateContextAsync(new BrowserContextOptions(), CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        Assert.NotNull(await page.QueryAsync(new Locator { Kind = LocatorKind.Ref, Value = "p1-v7-n3" }, CancellationToken.None));
    }
    [Fact]
    public async Task CreateContextAsync_MapsDescriptorToRemoteContext()
    {
        var broker = new RecordingBrowserBroker(command => Success(command, new BrowserContextDescriptor
        {
            ContextId = "ctx-1",
            UserDataDirectory = "C:/temp/browser",
            PageCount = 2
        }));
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);

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
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);

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
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);
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
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);

        var context = await runtime.GetContextAsync(
            new BrowserContextId("missing"), CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task BrokerFailure_PropagatesStableBrowserOperationException()
    {
        var broker = new RecordingBrowserBroker(command =>
            Failure(command, BrowserBridgeErrorCodes.BrowserNotAvailable));
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);

        var exception = await Assert.ThrowsAsync<BrowserOperationException>(
            () => runtime.ListContextsAsync(CancellationToken.None));

        Assert.Equal(BrowserBridgeErrorCodes.BrowserNotAvailable, exception.Code);
    }

    [Fact]
    public async Task Dispose_DoesNotCloseDesktopOwnedBrowserState()
    {
        var broker = new RecordingBrowserBroker(command => Success(command, new { }));
        var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);

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
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);
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
        var locateCommand = Assert.Single(broker.Commands.Where(c => c.Name == BrowserBridgeCommandNames.PageLocate));
        Assert.Equal(3, locateCommand.Arguments.GetProperty("expectedPageVersion").GetInt64());
        Assert.False(wait.TimedOut);
        Assert.Equal(
            [BrowserBridgeCommandNames.ContextCreate, BrowserBridgeCommandNames.PageCreate,
                BrowserBridgeCommandNames.PageSnapshot, BrowserBridgeCommandNames.PageLocate,
                BrowserBridgeCommandNames.PageInteract, BrowserBridgeCommandNames.PageWaitFor],
            broker.Commands.Select(command => command.Name));
    }

    [Fact]
    public async Task RemotePage_ScrollCarriesTheTargetModel_AndReadsBackTheFacts()
    {
        // 设计 §7：桥接路径也要携带滚动目标模型，并把宿主报告的结果事实带回来。
        BrowserBridgeCommand? sent = null;
        var broker = new RecordingBrowserBroker(command =>
        {
            sent = command;
            return command.Name switch
            {
                BrowserBridgeCommandNames.ContextCreate => Success(command, Context("ctx-1", 1)),
                BrowserBridgeCommandNames.PageCreate => Success(command, Page("ctx-1", "page-1", "https://example.test/")),
                BrowserBridgeCommandNames.PageInteract => Success(command, new BrowserInteractionResultDescriptor
                {
                    Element = null,
                    Page = Page("ctx-1", "page-1", "https://example.test/"),
                    // 宿主如实报告：滚到边界、0 位移、没有新内容 —— 事实，不是失败。
                    Scroll = new BrowserScrollReportDescriptor
                    {
                        Container = "document",
                        BeforeY = 4000,
                        AfterY = 4000,
                        AtBoundary = true,
                        ContentChanged = false,
                    },
                }),
                _ => Failure(command, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
            };
        });
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);
        var context = await runtime.CreateContextAsync(new BrowserContextOptions(), CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var outcome = await page.ScrollAsync(
            new ScrollOptions { Target = ScrollTarget.ToBottom, DeltaY = 800 },
            CancellationToken.None);

        // 事实原样带回（不是"滚了 0 像素"的编造，也不是 unreported）。
        Assert.Equal("document", outcome.Container);
        Assert.Equal(4000, outcome.AfterY);
        Assert.Equal(0, outcome.ConsumedDeltaY);
        Assert.True(outcome.AtBoundary);
        Assert.False(outcome.ContentChanged);

        // 目标模型确实上了线（断言线缆上的真实字段名）。
        using var document = JsonDocument.Parse(sent!.Arguments.GetRawText());
        var args = document.RootElement;
        Assert.Equal("scroll", args.GetProperty("action").GetString());
        Assert.Equal("to_bottom", args.GetProperty("scrollTarget").GetString());
        Assert.Equal(800, args.GetProperty("deltaY").GetDouble());
    }

    [Fact]
    public async Task RemotePage_ScrollWithoutHostFacts_IsMarkedUnreported()
    {
        // 宿主没报告事实时**不能编造**：容器标 unreported，位移全 0。
        var broker = new RecordingBrowserBroker(command => command.Name switch
        {
            BrowserBridgeCommandNames.ContextCreate => Success(command, Context("ctx-1", 1)),
            BrowserBridgeCommandNames.PageCreate => Success(command, Page("ctx-1", "page-1", "https://example.test/")),
            BrowserBridgeCommandNames.PageInteract => Success(command, new BrowserInteractionResultDescriptor
            {
                Element = null,
                Page = Page("ctx-1", "page-1", "https://example.test/"),
            }),
            _ => Failure(command, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
        });
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);
        var context = await runtime.CreateContextAsync(new BrowserContextOptions(), CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var outcome = await page.ScrollAsync(new ScrollOptions { DeltaY = 500 }, CancellationToken.None);

        Assert.Equal("unreported", outcome.Container);
        Assert.Equal(0, outcome.ConsumedDeltaY);
        Assert.False(outcome.AtBoundary);
    }

    [Fact]
    public async Task RemotePage_SnapshotCarriesTheScopeRefOverTheWire()
    {
        // 设计 §151：scope 必须在**两条传输**上都到达真正扫描页面的那一侧。
        BrowserBridgeCommand? sent = null;
        var broker = new RecordingBrowserBroker(command =>
        {
            if (command.Name == BrowserBridgeCommandNames.PageSnapshot)
            {
                sent = command;
            }

            return command.Name switch
            {
                BrowserBridgeCommandNames.ContextCreate => Success(command, Context("ctx-1", 1)),
                BrowserBridgeCommandNames.PageCreate => Success(command, Page("ctx-1", "page-1", "https://example.test/")),
                BrowserBridgeCommandNames.PageSnapshot => Success(command, new BrowserSnapshotDescriptor
                {
                    AccessibilityTree = "table ref=v1-n2 name=\"Rows\"",
                    NodeCount = 1,
                    PageVersion = 1,
                    // 宿主编出的修订与差异（§145）：Core 侧只做形状搬运。
                    Revision = "r1-1-abcdefgh-ijklmnop",
                    Changed = true,
                    Diff = new BrowserTextDiffDescriptor
                    {
                        Added = ["row ref=v1-n9 name=\"New\""],
                        Removed = [],
                        AddedCount = 3,
                        RemovedCount = 0,
                        Truncated = false,
                    },
                }),
                _ => Failure(command, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
            };
        });
        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: _callerAccessor);
        var context = await runtime.CreateContextAsync(new BrowserContextOptions(), CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await page.SnapshotAsync(
            new SnapshotOptions
            {
                IncludeDom = false,
                IncludeAccessibilityTree = true,
                ScopeRef = "p1-v1-n2",
                SinceRevision = "r1-1-abcdefgh-ijklmnop",
            },
            CancellationToken.None);

        Assert.Equal(1, snapshot.NodeCount);

        // 修订/变化/差异原样到达 Core（判定在宿主，搬运不能失真）。
        Assert.Equal("r1-1-abcdefgh-ijklmnop", snapshot.Revision);
        Assert.True(snapshot.Changed);
        Assert.Equal(["row ref=v1-n9 name=\"New\""], snapshot.Diff!.Added);
        Assert.Equal(3, snapshot.Diff.AddedCount);

        using var document = JsonDocument.Parse(sent!.Arguments.GetRawText());
        Assert.Equal("p1-v1-n2", document.RootElement.GetProperty("scopeRef").GetString());
        Assert.Equal("r1-1-abcdefgh-ijklmnop", document.RootElement.GetProperty("sinceRevision").GetString());
        Assert.False(document.RootElement.GetProperty("includeDom").GetBoolean());
        Assert.True(document.RootElement.GetProperty("includeAccessibilityTree").GetBoolean());
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
