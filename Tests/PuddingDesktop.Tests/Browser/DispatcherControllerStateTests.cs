using System.Collections.Concurrent;
using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;
using Xunit;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// Tests for BrowserBridgeCommandDispatcher and BrowserWorkspaceController
/// per document 79 §7.3: Origin projection, concurrent operation count,
/// pause/takeover rejection, control-state priority, handoff, target-close
/// summary clearing, view dispose unsubscription, plus sensitive-sentinel
/// assertions on Activity / Evidence serialized output.
/// </summary>
public class DispatcherControllerStateTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static BrowserBridgeCommand MakeCommand(
        string name,
        string? pageId = null,
        string? contextId = null,
        object? args = null,
        BrowserBridgeCommandOrigin? origin = null)
    {
        return new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = name,
            PageId = pageId,
            ContextId = contextId,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            Arguments = JsonSerializer.SerializeToElement(args ?? new { }),
            Origin = origin
        };
    }

    private static BrowserBridgeCommandOrigin MakeOrigin(
        string workspaceId = "ws-1",
        string agentInstanceId = "agent-1",
        string sessionId = "session-1",
        string? runId = "run-1",
        string? toolCallId = "call-1",
        string toolName = "browser_snapshot")
    {
        return new BrowserBridgeCommandOrigin
        {
            WorkspaceId = workspaceId,
            AgentInstanceId = agentInstanceId,
            SessionId = sessionId,
            RunId = runId,
            ToolCallId = toolCallId,
            ToolName = toolName
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pwtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Serializes an AgentBrowserActivitySnapshot to indented JSON for sentinel scanning.
    /// </summary>
    private static string SerializeActivity(AgentBrowserActivitySnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    /// <summary>
    /// Sensitive sentinel strings that MUST NOT appear in any serialized
    /// Activity or Evidence output (document 79 §7.3).
    /// </summary>
    private static readonly string[] SensitiveSentinels =
    [
        "SECRET_FILL_VALUE",
        "Authorization",
        "Cookie",
        "api-key",
        "access_token"
    ];

    private static void AssertNoSentinels(string json, string context)
    {
        foreach (var sentinel in SensitiveSentinels)
        {
            Assert.DoesNotContain(sentinel, json, StringComparison.OrdinalIgnoreCase);
        }
        // Also verify no raw argument payload leaks through
        Assert.DoesNotContain("\"fillValue\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"fill_value\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"textValue\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 1: Dispatcher projects Origin fields into Activity WITHOUT command args
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Dispatcher_ProjectsOriginIntoActivityWithoutArguments()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());

        var snapshots = new List<AgentBrowserActivitySnapshot>();
        dispatcher.ActivityChanged += (_, args) => snapshots.Add(args.Snapshot);

        var origin = MakeOrigin(
            agentInstanceId: "agent-42",
            sessionId: "sess-abc",
            runId: "run-xyz",
            toolCallId: "tc-001",
            toolName: "page.goto");

        // Arguments contain a sentinel fill value that MUST NOT leak into Activity
        var cmd = MakeCommand(
            BrowserBridgeCommandNames.PageGoto,
            pageId: "page-1",
            args: new
            {
                url = "https://example.com",
                fillValue = "SECRET_FILL_VALUE",
                Authorization = "Bearer secret-token-123",
                Cookie = "session=abc123",
                api_key = "key-deadbeef",
                access_token = "tok-12345678"
            },
            origin: origin);

        var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, snapshots.Count); // start + completion

        // Completion snapshot must carry Origin fields
        var completed = snapshots[1];
        Assert.True(completed.IsCompleted);
        Assert.Equal("agent-42", completed.AgentInstanceId);
        Assert.Equal("sess-abc", completed.SessionId);
        Assert.Equal("run-xyz", completed.RunId);
        Assert.Equal("tc-001", completed.ToolCallId);
        Assert.Equal("page.goto", completed.ToolName);

        // Serialized Activity JSON MUST NOT contain command arguments or sentinels
        var json = SerializeActivity(completed);
        AssertNoSentinels(json, "Activity snapshot after Origin projection");

        // Also verify Target is resolved (PageId present, so Target = page-1)
        Assert.Equal("page-1", completed.Target);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 2: Concurrent active operation count is tracked accurately
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Dispatcher_TracksConcurrentActiveOperationCount()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        var handler = new GatedBrowserHandler();
        dispatcher.SetHandler(handler);

        var stateSnapshots = new ConcurrentBag<AgentBrowserOperationStateSnapshot>();
        dispatcher.OperationStateChanged += (_, args) =>
            stateSnapshots.Add(args.Snapshot);

        // Start 3 concurrent commands — they will block inside the handler
        var tasks = new List<Task<BrowserBridgeCommandResult>>();
        for (int i = 0; i < 3; i++)
        {
            var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
            tasks.Add(dispatcher.DispatchAsync(cmd, CancellationToken.None));
        }

        // Wait until 3 commands have entered the handler
        await handler.WaitForEnteredCountAsync(3, TimeSpan.FromSeconds(5));

        // Allow a short window for OperationStateChanged events to propagate
        await Task.Delay(100);

        // Gather snapshots while commands are all active
        var activeSnapshots = stateSnapshots.ToArray();

        // At least one snapshot should have ActiveOperationCount == 3
        Assert.Contains(activeSnapshots, s => s.ActiveOperationCount == 3);

        // Release all commands
        handler.ReleaseAll();

        await Task.WhenAll(tasks);

        // Final snapshot(s) after all complete should include count == 0
        var allSnapshots = stateSnapshots.ToArray();
        Assert.Contains(allSnapshots, s => s.ActiveOperationCount == 0);

        // Verify the count never exceeded 3
        Assert.All(allSnapshots, s => Assert.True(s.ActiveOperationCount <= 3));
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 3: Paused → rejected command still produces a failure Activity
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PausedCommand_ProducesSanitizedFailureActivity()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());
        dispatcher.SetPaused(true);

        var snapshots = new List<AgentBrowserActivitySnapshot>();
        dispatcher.ActivityChanged += (_, args) => snapshots.Add(args.Snapshot);

        var origin = MakeOrigin(
            agentInstanceId: "agent-paused",
            toolName: "page.goto");

        var cmd = MakeCommand(
            BrowserBridgeCommandNames.PageGoto,
            pageId: "page-99",
            args: new
            {
                url = "https://example.com",
                secretPayload = "SECRET_FILL_VALUE",
                Authorization = "Bearer leaked-token"
            },
            origin: origin);

        var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

        // Command must be rejected
        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserPaused, result.ErrorCode);

        // A single Activity must be published (the failure activity, recorded before returning)
        Assert.Single(snapshots);
        var activity = snapshots[0];
        Assert.False(activity.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserPaused, activity.ErrorCode);

        // Origin fields must be present even in failure activities
        Assert.Equal("agent-paused", activity.AgentInstanceId);
        Assert.Equal("page.goto", activity.ToolName);

        // Target must be resolved
        Assert.Equal("page-99", activity.Target);

        // Serialized output MUST NOT contain sentinel strings
        var json = SerializeActivity(activity);
        AssertNoSentinels(json, "Paused failure Activity");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 4: UserTakeover → rejected command produces failure Activity
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserTakeoverCommand_ProducesSanitizedFailureActivity()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());
        dispatcher.SetUserTakeover(true);

        var snapshots = new List<AgentBrowserActivitySnapshot>();
        dispatcher.ActivityChanged += (_, args) => snapshots.Add(args.Snapshot);

        var origin = MakeOrigin(
            agentInstanceId: "agent-takeover",
            toolName: "page.click");

        var cmd = MakeCommand(
            BrowserBridgeCommandNames.PageInteract,
            pageId: "page-42",
            args: new
            {
                action = "fill",
                textValue = "SECRET_FILL_VALUE",
                Cookie = "auth=leaked",
                api_key = "sk-12345",
                access_token = "ghp_fake"
            },
            origin: origin);

        var result = await dispatcher.DispatchAsync(cmd, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, result.ErrorCode);

        Assert.Single(snapshots);
        var activity = snapshots[0];
        Assert.False(activity.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserUserTakeover, activity.ErrorCode);
        Assert.Equal("agent-takeover", activity.AgentInstanceId);
        Assert.Equal("page.click", activity.ToolName);
        Assert.Equal("page-42", activity.Target);

        var json = SerializeActivity(activity);
        AssertNoSentinels(json, "UserTakeover failure Activity");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 5: Controller control-state priority: UserTakeover > Paused > AgentControlling > Idle
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Controller_UsesAutomaticControlStatePriority()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            // 1. Start from Idle
            Assert.Equal(AgentBrowserControlState.Idle, controller.ControlState);

            // 2. Active operations → AgentControlling
            await controller.SetPausedAsync(false, CancellationToken.None);
            await controller.SetUserTakeoverAsync(false, CancellationToken.None);
            await controller.ApplyOperationStateAsync(
                new AgentBrowserOperationStateSnapshot
                {
                    ActiveOperationCount = 2,
                    MostRecentOrigin = MakeOrigin(agentInstanceId: "agent-a", toolName: "page.snapshot")
                }, CancellationToken.None);
            Assert.Equal(AgentBrowserControlState.AgentControlling, controller.ControlState);

            // 3. Paused + active ops → Paused wins over AgentControlling
            await controller.SetPausedAsync(true, CancellationToken.None);
            Assert.Equal(AgentBrowserControlState.Paused, controller.ControlState);

            // 4. UserTakeover + paused + active → UserTakeover wins
            await controller.SetUserTakeoverAsync(true, CancellationToken.None);
            Assert.Equal(AgentBrowserControlState.UserTakeover, controller.ControlState);

            // 5. Remove takeover, still paused → Paused
            await controller.SetUserTakeoverAsync(false, CancellationToken.None);
            Assert.Equal(AgentBrowserControlState.Paused, controller.ControlState);

            // 6. Remove pause, still active → AgentControlling
            await controller.SetPausedAsync(false, CancellationToken.None);
            Assert.Equal(AgentBrowserControlState.AgentControlling, controller.ControlState);

            // 7. Active operation count drops to zero → Idle
            await controller.ApplyOperationStateAsync(
                new AgentBrowserOperationStateSnapshot
                {
                    ActiveOperationCount = 0,
                    MostRecentOrigin = null
                }, CancellationToken.None);
            Assert.Equal(AgentBrowserControlState.Idle, controller.ControlState);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 6: AssignAgentTargetAsync sets target but does NOT force AgentControlling
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Controller_HandoffSetsTargetButRemainsIdle()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            // Create a page — it becomes active but control state remains Idle
            var pageId = await controller.CreatePageAsync(null, activate: true);

            // Handoff: assign agent target
            await controller.AssignAgentTargetAsync(pageId, CancellationToken.None);

            // AgentTargetPageId is set
            Assert.NotNull(controller.AgentTargetPageId);
            Assert.Equal(pageId, controller.AgentTargetPageId);

            // ControlState MUST remain Idle — handoff does not auto-promote to AgentControlling
            Assert.Equal(AgentBrowserControlState.Idle, controller.ControlState);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 7: Closing the Agent Target page clears CurrentAgentSummary
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Controller_ClearsAgentSummaryWhenTargetCloses()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            // Apply an operation state to set a summary
            await controller.ApplyOperationStateAsync(
                new AgentBrowserOperationStateSnapshot
                {
                    ActiveOperationCount = 1,
                    MostRecentOrigin = MakeOrigin(agentInstanceId: "agent-sum", toolName: "page.snapshot")
                }, CancellationToken.None);

            // Summary should be populated
            Assert.Equal("agent-sum · page.snapshot", controller.CurrentAgentSummary);

            // Create a page and assign as agent target
            var pageId = await controller.CreatePageAsync(null, activate: true);
            await controller.AssignAgentTargetAsync(pageId, CancellationToken.None);

            // Close the target page (this should clear the summary)
            await controller.ClosePageAsync(pageId, CancellationToken.None);

            // CurrentAgentSummary must be cleared to "-"
            Assert.Equal("-", controller.CurrentAgentSummary);
            Assert.Null(controller.AgentTargetPageId);
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Test 8: View dispose → unsubscribes from OperationStateChanged events
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ViewDispose_UnsubscribesOperationStateEvents()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());

        // Simulate a View that subscribes to OperationStateChanged
        var callbackCount = 0;
        var disposed = false;

        void OnOperationStateChanged(object? sender, AgentBrowserOperationStateChangedEventArgs args)
        {
            if (!disposed)
                Interlocked.Increment(ref callbackCount);
        }

        dispatcher.OperationStateChanged += OnOperationStateChanged;

        // Dispatch a command — callback should fire twice (enter + exit)
        var cmd = MakeCommand(BrowserBridgeCommandNames.ContextCreate);
        await dispatcher.DispatchAsync(cmd, CancellationToken.None);

        var countBeforeDispose = Volatile.Read(ref callbackCount);
        Assert.True(countBeforeDispose >= 2, $"Expected >= 2 callbacks, got {countBeforeDispose}");

        // Simulate View dispose: unsubscribe and set disposed flag
        dispatcher.OperationStateChanged -= OnOperationStateChanged;
        disposed = true;
        var countAfterUnsubscribe = Volatile.Read(ref callbackCount);

        // Dispatch another command — no additional callbacks should fire
        await dispatcher.DispatchAsync(
            cmd with { OperationId = Guid.NewGuid() }, CancellationToken.None);

        var countFinal = Volatile.Read(ref callbackCount);
        Assert.Equal(countAfterUnsubscribe, countFinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Sentinels: Evidence document MUST NOT leak sensitive values
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SensitiveSentinel_NotInEvidenceExport()
    {
        var runtime = new FakeBrowserRuntime();
        var controller = new BrowserWorkspaceController(
            runtime, new FakeBrowserSurfaceHost(), new FakeUiDispatcher());
        var tempDir = CreateTempDirectory();

        try
        {
            await controller.InitializeAsync(tempDir, CancellationToken.None);

            // Apply activities where Target contains sentinel-like substrings.
            // Since activities sanitize what they store, the evidence export
            // must not contain the sentinels.
            await controller.ApplyActivityAsync(new AgentBrowserActivitySnapshot
            {
                OperationId = Guid.NewGuid(),
                CommandName = "page.fill",
                Target = "page-secret-SECRET_FILL_VALUE",
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1),
                Success = true,
                AgentInstanceId = "agent-auth",
                SessionId = "sess-Cookie-abc",
                RunId = "run-api-key-xyz",
                ToolCallId = "tc-access_token-001",
                ToolName = "page.interact"
            }, CancellationToken.None);

            // Capture evidence via the controller
            var evidence = await controller.CaptureActivityEvidenceAsync(
                DateTimeOffset.UtcNow, CancellationToken.None);

            // Serialize the evidence document
            var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            // Assert NO sentinel strings appear anywhere in the evidence JSON
            AssertNoSentinels(json, "Evidence document");

            // Also: activity items in evidence do NOT carry AgentInstanceId/etc.
            // (BrowserActivityEvidenceItem only has OperationId, CommandName,
            //  Target, StartedAt, CompletedAt, Success, ErrorCode)
            foreach (var sentinel in new[] { "agentInstanceId", "sessionId", "runId", "toolCallId", "toolName" })
            {
                Assert.DoesNotContain($"\"{sentinel}\"", json, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SensitiveSentinel_NotInActivitySerialization_AfterFullDispatch()
    {
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulBrowserHandler());

        var snapshots = new List<AgentBrowserActivitySnapshot>();
        dispatcher.ActivityChanged += (_, args) => snapshots.Add(args.Snapshot);

        // Command arguments stuffed with every sensitive sentinel
        var origin = MakeOrigin(agentInstanceId: "agent-1", toolName: "page.fill");
        var cmd = MakeCommand(
            BrowserBridgeCommandNames.PageInteract,
            pageId: "page-sentinel",
            args: new
            {
                action = "fill",
                text = "SECRET_FILL_VALUE",
                Authorization = "Bearer abcdef123456",
                Cookie = "sessionid=leaked; secure",
                api_key = "sk-proj-deadbeef",
                access_token = "ya29.fake-token-value"
            },
            origin: origin);

        await dispatcher.DispatchAsync(cmd, CancellationToken.None);

        // Both snapshots (start + completion) must be sentinel-free
        foreach (var snapshot in snapshots)
        {
            var json = SerializeActivity(snapshot);
            AssertNoSentinels(json, $"Activity snapshot (completed={snapshot.IsCompleted})");
        }
    }
}

// ─── Test-specific fake handler: gates N commands then releases them all ─────

/// <summary>
/// Handler that blocks the first N commands behind a gate, then releases
/// all at once. Used to test concurrent ActiveOperationCount tracking.
/// </summary>
internal sealed class GatedBrowserHandler : IBrowserCommandHandler
{
    private readonly SemaphoreSlim _enterGate = new(0);
    private readonly TaskCompletionSource _allReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _enteredCount;

    public int EnteredCount => Volatile.Read(ref _enteredCount);

    public async Task WaitForEnteredCountAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (Volatile.Read(ref _enteredCount) < count && !cts.IsCancellationRequested)
            await Task.Delay(10, cts.Token);
    }

    public void ReleaseAll()
    {
        _allReleased.TrySetResult();
    }

    public async Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        Interlocked.Increment(ref _enteredCount);
        _enterGate.Release(); // signal one waiter

        // Block until released or cancelled
        await _allReleased.Task.WaitAsync(ct);

        return new BrowserBridgeCommandResult
        {
            OperationId = command.OperationId,
            Success = true,
            Value = JsonSerializer.SerializeToElement(new { ok = true })
        };
    }
}
