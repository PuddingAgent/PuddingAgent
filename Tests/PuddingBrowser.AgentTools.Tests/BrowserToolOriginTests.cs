
using PuddingBrowser.Abstractions;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools.Tests;

/// <summary>
/// Tests verifying that BrowserAgentToolBase pushes and restores BrowserOperationOrigin
/// via the AsyncLocal-based BrowserOperationOriginAccessor, and that non-browser tools
/// are unaffected by the execution scope hook (document 79 section 7.1).
/// </summary>
public sealed class BrowserToolOriginTests
{
    /// <summary>
    /// When a browser tool executes, it must push an origin derived from ToolExecutionRequest
    /// into IBrowserOperationOriginAccessor so that IBrowserRuntime implementations can read
    /// the calling Agent identity during the call.
    /// </summary>
    [Fact]
    public async Task BrowserTool_PushesAgentOriginDuringRuntimeCall()
    {
        var accessor = new BrowserOperationOriginAccessor();
        var runtime = new OriginCapturingBrowserRuntime(accessor);
        var tool = new TestOriginBrowserTool(runtime, accessor);

        var request = new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-instance-1",
                ConfigurationAgentInstanceId = "agent-config-1",
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = "conv-1",
                    RunId = "run-1",
                    ToolCallId = "call-1"
                }
            }
        };

        await tool.ExecuteAsync(request);

        Assert.NotNull(runtime.CapturedOrigin);
        Assert.Equal("ws-1", runtime.CapturedOrigin!.WorkspaceId);
        // ConfigurationAgentInstanceId takes precedence over AgentInstanceId
        Assert.Equal("agent-config-1", runtime.CapturedOrigin.AgentInstanceId);
        Assert.Equal("session-1", runtime.CapturedOrigin.SessionId);
        Assert.Equal("conv-1", runtime.CapturedOrigin.ConversationId);
        Assert.Equal("run-1", runtime.CapturedOrigin.RunId);
        Assert.Equal("call-1", runtime.CapturedOrigin.ToolCallId);
        Assert.Equal("test_origin_browser", runtime.CapturedOrigin.ToolName);
    }

    /// <summary>
    /// After a successful browser tool execution, the origin must be popped from the
    /// AsyncLocal stack so that IBrowserOperationOriginAccessor.Current returns to its
    /// previous value. When no outer scope is active it restores to null; when an outer
    /// origin was pre-pushed it restores to that outer origin (nested push/pop).
    /// </summary>
    [Fact]
    public async Task BrowserTool_RestoresPreviousOriginAfterSuccess()
    {
        var accessor = new BrowserOperationOriginAccessor();
        var runtime = new OriginCapturingBrowserRuntime(accessor);
        var tool = new TestOriginBrowserTool(runtime, accessor);

        // ── Case 1: no outer scope → restore to null ──
        Assert.Null(accessor.Current);

        await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-2",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-2",
                SessionId = "session-2",
                AgentInstanceId = "agent-2"
            }
        });

        // After execution, Current must be restored to null
        Assert.Null(accessor.Current);
        Assert.NotNull(runtime.CapturedOrigin);
        Assert.Equal("agent-2", runtime.CapturedOrigin!.AgentInstanceId);

        // ── Case 2: nested — pre-push outer origin, tool pushes inner, restore to outer ──
        var outerOrigin = new BrowserOperationOrigin
        {
            WorkspaceId = "outer-ws",
            AgentInstanceId = "outer-agent",
            SessionId = "outer-session",
            ToolName = "outer_tool"
        };
        using (accessor.Push(outerOrigin))
        {
            Assert.Same(outerOrigin, accessor.Current);

            var runtime2 = new OriginCapturingBrowserRuntime(accessor);
            var tool2 = new TestOriginBrowserTool(runtime2, accessor);
            await tool2.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-2b",
                ArgumentsJson = "{}",
                Context = new ToolExecutionContext
                {
                    WorkspaceId = "inner-ws",
                    SessionId = "inner-session",
                    AgentInstanceId = "inner-agent"
                }
            });

            // During execution the runtime saw the inner origin
            Assert.NotNull(runtime2.CapturedOrigin);
            Assert.Equal("inner-agent", runtime2.CapturedOrigin!.AgentInstanceId);

            // After nested execution, Current must be restored to outer origin
            Assert.Same(outerOrigin, accessor.Current);
        }

        // After outer dispose, back to null
        Assert.Null(accessor.Current);
    }

    /// <summary>
    /// When a browser tool's ExecuteCoreAsync throws (e.g. a runtime failure), the using
    /// scope in PuddingToolBase.ExecuteAsync still disposes, restoring the origin.
    /// The origin must have been pushed during the call and restored afterwards.
    /// </summary>
    [Fact]
    public async Task BrowserTool_RestoresPreviousOriginAfterFailure()
    {
        var accessor = new BrowserOperationOriginAccessor();
        var runtime = new OriginCapturingBrowserRuntime(accessor, throwOnCall: true);
        var tool = new TestOriginBrowserTool(runtime, accessor);

        Assert.Null(accessor.Current);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-3",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-3",
                SessionId = "session-3",
                AgentInstanceId = "agent-3"
            }
        });

        // Tool should report failure
        Assert.False(result.Success);

        // Origin must be restored even after failure
        Assert.Null(accessor.Current);

        // Origin was pushed during the call (captured before the throw)
        Assert.NotNull(runtime.CapturedOrigin);
        Assert.Equal("agent-3", runtime.CapturedOrigin!.AgentInstanceId);
    }

    /// <summary>
    /// AsyncLocal isolates per-execution-context. Two concurrent browser tool calls
    /// with different AgentInstanceId/SessionId must each see only their own origin
    /// inside the runtime call — no cross-leakage.
    /// </summary>
    [Fact]
    public async Task ConcurrentBrowserToolCalls_DoNotLeakOriginAcrossAgents()
    {
        var accessor = new BrowserOperationOriginAccessor();

        var runtime1 = new OriginCapturingBrowserRuntime(accessor);
        var runtime2 = new OriginCapturingBrowserRuntime(accessor);

        var tool1 = new TestOriginBrowserTool(runtime1, accessor);
        var tool2 = new TestOriginBrowserTool(runtime2, accessor);

        var request1 = new ToolExecutionRequest
        {
            ToolCallId = "call-a",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-a",
                SessionId = "session-a",
                AgentInstanceId = "agent-a",
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = "conv-a",
                    RunId = "run-a",
                    ToolCallId = "call-a"
                }
            }
        };

        var request2 = new ToolExecutionRequest
        {
            ToolCallId = "call-b",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-b",
                SessionId = "session-b",
                AgentInstanceId = "agent-b",
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.SubAgent,
                    ConversationId = "conv-b",
                    RunId = "run-b",
                    ToolCallId = "call-b"
                }
            }
        };

        await Task.WhenAll(
            tool1.ExecuteAsync(request1),
            tool2.ExecuteAsync(request2));

        // Each runtime saw only its own Agent's origin
        Assert.NotNull(runtime1.CapturedOrigin);
        Assert.Equal("agent-a", runtime1.CapturedOrigin!.AgentInstanceId);
        Assert.Equal("session-a", runtime1.CapturedOrigin.SessionId);
        Assert.Equal("conv-a", runtime1.CapturedOrigin.ConversationId);
        Assert.Equal("run-a", runtime1.CapturedOrigin.RunId);

        Assert.NotNull(runtime2.CapturedOrigin);
        Assert.Equal("agent-b", runtime2.CapturedOrigin!.AgentInstanceId);
        Assert.Equal("session-b", runtime2.CapturedOrigin.SessionId);
        Assert.Equal("conv-b", runtime2.CapturedOrigin.ConversationId);
        Assert.Equal("run-b", runtime2.CapturedOrigin.RunId);

        // After both complete, Current is null
        Assert.Null(accessor.Current);
    }

    /// <summary>
    /// PuddingToolBase.BeginExecutionScope returns null by default. Non-browser tools
    /// that do not override it must never push an origin into the accessor.
    /// </summary>
    [Fact]
    public async Task NonBrowserTool_IsUnaffectedByExecutionScopeHook()
    {
        var accessor = new BrowserOperationOriginAccessor();
        var tool = new NonBrowserTestTool(accessor);

        Assert.Null(accessor.Current);

        await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-4",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-4",
                SessionId = "session-4",
                AgentInstanceId = "agent-4",
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = "conv-4",
                    RunId = "run-4",
                    ToolCallId = "call-4"
                }
            }
        });

        // Non-browser tool must never push an origin
        Assert.Null(tool.CapturedOrigin);
        Assert.Null(accessor.Current);
    }

    // ── Test fakes and helpers ────────────────────────────────────────

    /// <summary>
    /// A minimal IBrowserRuntime that captures the current
    /// <see cref="IBrowserOperationOriginAccessor.Current"/> when any runtime method
    /// is called. Optionally throws to simulate runtime failure.
    /// </summary>
    private sealed class OriginCapturingBrowserRuntime : IBrowserRuntime
    {
        private readonly IBrowserOperationOriginAccessor _accessor;
        private readonly bool _throwOnCall;

        public OriginCapturingBrowserRuntime(
            IBrowserOperationOriginAccessor accessor,
            bool throwOnCall = false)
        {
            _accessor = accessor;
            _throwOnCall = throwOnCall;
        }

        public BrowserOperationOrigin? CapturedOrigin { get; private set; }

        public BrowserRuntimeState State => BrowserRuntimeState.Created;

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
        {
            CapturedOrigin = _accessor.Current;
            if (_throwOnCall)
                throw new InvalidOperationException("Simulated browser runtime failure");
            return Task.FromResult<IReadOnlyList<BrowserContextInfo>>(Array.Empty<BrowserContextInfo>());
        }

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct)
            => ThrowNotSupported<IBrowserContext>();

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
            => ThrowNotSupported<IBrowserContext?>();

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
            => ThrowNotSupported();

        public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
            BrowserEventFilter filter,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task ThrowNotSupported()
            => Task.FromException(new NotSupportedException());

        private static Task<T> ThrowNotSupported<T>()
            => Task.FromException<T>(new NotSupportedException());
    }

    /// <summary>
    /// Minimal args record for the test browser tool.
    /// </summary>
    private sealed record TestOriginToolArgs
    {
        public string? Dummy { get; init; }
    }

    /// <summary>
    /// A browser tool used solely for origin verification. Calls
    /// <c>ListContextsAsync</c> on the injected runtime so that the fake runtime
    /// can capture the current origin during execution.
    /// </summary>
    [Tool(
        id: "test_origin_browser",
        name: "Test Origin Browser",
        description: "Test tool for browser operation origin verification.",
        category: ToolCategory.General)]
    private sealed class TestOriginBrowserTool(
        IBrowserRuntime runtime,
        IBrowserOperationOriginAccessor originAccessor)
        : BrowserAgentToolBase<TestOriginToolArgs>(originAccessor)
    {
        private readonly IBrowserRuntime _runtime = runtime;

        protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
            TestOriginToolArgs args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            await _runtime.ListContextsAsync(ct);
            return ToolExecutionResult.Ok("{}");
        }
    }

    /// <summary>
    /// Minimal args record for the non-browser test tool.
    /// </summary>
    private sealed record NonBrowserTestArgs
    {
        public string? Dummy { get; init; }
    }

    /// <summary>
    /// A non-browser tool that does NOT override <c>BeginExecutionScope</c>.
    /// It captures <see cref="IBrowserOperationOriginAccessor.Current"/> during
    /// <c>ExecuteCoreAsync</c> to prove that the default hook leaves the origin
    /// untouched.
    /// </summary>
    [Tool(
        id: "test_non_browser",
        name: "Test Non-Browser",
        description: "Test tool proving non-browser tools do not push an origin.",
        category: ToolCategory.General)]
    private sealed class NonBrowserTestTool(
        IBrowserOperationOriginAccessor accessor)
        : PuddingToolBase<NonBrowserTestArgs>
    {
        public BrowserOperationOrigin? CapturedOrigin { get; private set; }

        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            NonBrowserTestArgs args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            CapturedOrigin = accessor.Current;
            return Task.FromResult(ToolExecutionResult.Ok("{}"));
        }
    }
}
