using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class RemoteBrowserRuntimeOriginTests
{
    [Fact]
    public async Task RemoteRuntime_CopiesCurrentOriginIntoEveryBridgeCommand()
    {
        // Arrange
        var accessor = new BrowserOperationOriginAccessor();
        var origin = new BrowserOperationOrigin
        {
            WorkspaceId = "ws-origin-1",
            AgentInstanceId = "agent-7f3a",
            SessionId = "sess-d91e",
            ConversationId = "conv-42",
            RunId = "run-8b",
            ToolCallId = "tc-19",
            ToolName = "browse_text"
        };

        var broker = new FakeOriginBroker(cmd => Success(cmd, new BrowserContextListDescriptor
        {
            Contexts = []
        }));

        // Act: push origin then execute any browser operation
        using (accessor.Push(origin))
        {
            await using var runtime = new RemoteBrowserRuntime(broker, accessor);
            await runtime.ListContextsAsync(CancellationToken.None);
        }

        // Assert: the broker received exactly one command with a non-null Origin
        var command = Assert.Single(broker.Commands);
        Assert.NotNull(command.Origin);
        Assert.Equal("ws-origin-1", command.Origin!.WorkspaceId);
        Assert.Equal("agent-7f3a", command.Origin.AgentInstanceId);
        Assert.Equal("sess-d91e", command.Origin.SessionId);
        Assert.Equal("conv-42", command.Origin.ConversationId);
        Assert.Equal("run-8b", command.Origin.RunId);
        Assert.Equal("tc-19", command.Origin.ToolCallId);
        Assert.Equal("browse_text", command.Origin.ToolName);
    }

    [Fact]
    public async Task RemotePage_DoesNotCacheOriginFromCreatingAgent()
    {
        // Arrange
        var accessor = new BrowserOperationOriginAccessor();
        var originA = new BrowserOperationOrigin
        {
            WorkspaceId = "ws-1",
            AgentInstanceId = "agent-a",
            SessionId = "sess-a",
            ToolName = "tool_a"
        };
        var originB = new BrowserOperationOrigin
        {
            WorkspaceId = "ws-1",
            AgentInstanceId = "agent-b",
            SessionId = "sess-b",
            ToolName = "tool_b"
        };

        var broker = new FakeOriginBroker(cmd => cmd.Name switch
        {
            BrowserBridgeCommandNames.ContextCreate => Success(cmd, Ctx("ctx-1", 0)),
            BrowserBridgeCommandNames.PageCreate => Success(cmd, Pg("ctx-1", "page-1", "about:blank")),
            BrowserBridgeCommandNames.PageSnapshot => Success(cmd, new BrowserSnapshotDescriptor
            {
                NodeCount = 1,
                PageVersion = 5
            }),
            _ => Failure(cmd, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
        });

        await using var runtime = new RemoteBrowserRuntime(broker, accessor);

        // ① Agent A scope: create context and page
        IBrowserPage page;
        using (accessor.Push(originA))
        {
            var context = await runtime.CreateContextAsync(
                new BrowserContextOptions(), CancellationToken.None);
            page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        }

        // ② Exit A scope (Dispose above) — accessor.Current is now null

        // ③ Agent B scope: call Snapshot on the SAME page
        using (accessor.Push(originB))
        {
            await page.SnapshotAsync(new SnapshotOptions(), CancellationToken.None);
        }

        // ④ Assert: the Snapshot command (index 2) carries origin B, not A
        Assert.Equal(3, broker.Commands.Count);
        var snapshotCmd = broker.Commands[2];
        Assert.NotNull(snapshotCmd.Origin);
        Assert.Equal("agent-b", snapshotCmd.Origin!.AgentInstanceId);
        Assert.Equal("sess-b", snapshotCmd.Origin.SessionId);
        Assert.Equal("tool_b", snapshotCmd.Origin.ToolName);
    }

    [Fact]
    public async Task RemoteRuntime_AllowsNullOriginForNonAgentCaller()
    {
        // Arrange: create an accessor but never push any origin
        var accessor = new BrowserOperationOriginAccessor();
        var broker = new FakeOriginBroker(cmd => Success(cmd, new BrowserContextListDescriptor
        {
            Contexts = []
        }));

        // Act: execute without pushing an origin (simulates non-Agent caller)
        await using var runtime = new RemoteBrowserRuntime(broker, accessor);
        await runtime.ListContextsAsync(CancellationToken.None);

        // Assert: the broker command must have Origin == null
        var command = Assert.Single(broker.Commands);
        Assert.Null(command.Origin);
    }

    [Fact]
    public async Task AgentTool_ThroughAuthenticatedBridge_PreservesOriginAndResult()
    {
        // Arrange
        var accessor = new BrowserOperationOriginAccessor();
        var origin = new BrowserOperationOrigin
        {
            WorkspaceId = "ws-full",
            AgentInstanceId = "agent-full",
            SessionId = "sess-full",
            ConversationId = "conv-full",
            RunId = "run-full",
            ToolCallId = "tc-full",
            ToolName = "browse_navigate"
        };

        const long expectedPageVersion = 42;

        var broker = new FakeOriginBroker(cmd => cmd.Name switch
        {
            BrowserBridgeCommandNames.ContextCreate => Success(cmd, Ctx("ctx-full", 0)),
            BrowserBridgeCommandNames.PageCreate => Success(cmd,
                Pg("ctx-full", "page-full", "about:blank")),
            BrowserBridgeCommandNames.PageGoto => Success(cmd, new BrowserNavigationResultDescriptor
            {
                Url = "https://example.com/",
                Ok = true,
                StatusCode = 200,
                Page = Pg("ctx-full", "page-full", "https://example.com/") with
                {
                    PageVersion = expectedPageVersion,
                    CanGoBack = true,
                    Title = "Example Domain"
                }
            }),
            _ => Failure(cmd, BrowserBridgeErrorCodes.BrowserOperationNotSupported)
        });

        // Act: full round-trip through the authenticated bridge
        using (accessor.Push(origin))
        {
            await using var runtime = new RemoteBrowserRuntime(broker, accessor);
            var context = await runtime.CreateContextAsync(
                new BrowserContextOptions(), CancellationToken.None);
            var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

            var nav = await page.GotoAsync(
                new Uri("https://example.com"),
                new NavigationOptions(),
                CancellationToken.None);

            // Assert: structured result (PageVersion) returned to caller
            Assert.True(nav.Ok);
            Assert.Equal(200, nav.StatusCode);
            Assert.Equal(expectedPageVersion, page.PageVersion);
        }

        // Assert: every command captured by the broker has the correct Origin
        Assert.Equal(3, broker.Commands.Count);
        foreach (var cmd in broker.Commands)
        {
            Assert.NotNull(cmd.Origin);
            Assert.Equal("ws-full", cmd.Origin!.WorkspaceId);
            Assert.Equal("agent-full", cmd.Origin.AgentInstanceId);
            Assert.Equal("sess-full", cmd.Origin.SessionId);
            Assert.Equal("conv-full", cmd.Origin.ConversationId);
            Assert.Equal("run-full", cmd.Origin.RunId);
            Assert.Equal("tc-full", cmd.Origin.ToolCallId);
            Assert.Equal("browse_navigate", cmd.Origin.ToolName);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static BrowserContextDescriptor Ctx(string contextId, int pageCount) => new()
    {
        ContextId = contextId,
        UserDataDirectory = "C:/fake/browser",
        PageCount = pageCount
    };

    private static BrowserPageDescriptor Pg(string contextId, string pageId, string url) => new()
    {
        ContextId = contextId,
        PageId = pageId,
        Title = "Test",
        Url = url,
        PageVersion = 1
    };

    private static BrowserBridgeCommandResult Success(
        BrowserBridgeCommand command, object value) => new()
    {
        OperationId = command.OperationId,
        Success = true,
        Value = JsonSerializer.SerializeToElement(
            value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private static BrowserBridgeCommandResult Failure(
        BrowserBridgeCommand command, string code) => new()
    {
        OperationId = command.OperationId,
        Success = false,
        ErrorCode = code,
        ErrorMessage = "scripted failure"
    };

    // ── Fake broker ───────────────────────────────────────────────────────

    private sealed class FakeOriginBroker(
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

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void HandleResult(
            Guid connectionId, long generation, BrowserBridgeCommandResult result) { }

        public void FailPendingForConnection(
            Guid connectionId, long generation, string errorCode, string message) { }
    }
}
