using System.Text.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// Doc 79 Section 7.4: deterministic composition test that traverses the real
/// IPuddingToolExecutionService permission entry point, a real Browser Tool Registry,
/// a real AgentFirewall, and the authenticated Browser Bridge — without calling
/// any live model. This is the canonical "full chain" browser-tool integration test.
/// </summary>
public sealed class ConfiguredAgentBrowserSnapshotPolicyExecutionTests
{
    /// <summary>
    /// Verifies the complete execution path:
    ///   1. Profile/registry exposes browser_snapshot
    ///   2. Capability policy permits invocation through the real firewall
    ///   3. Browser bridge receives authenticated commands with correct Origin
    ///   4. Structured snapshot result (PageVersion) flows back to the caller
    ///   5. Activity output contains no raw arguments or DOM sentinel leakage
    /// </summary>
    [Fact]
    public async Task ConfiguredAgent_BrowserSnapshotTool_TraversesPolicyExecutionAndAuthenticatedBridge()
    {
        // ── Arrange: host, socket, and execution-service entry point ──────────
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        var ack = await BrowserBridgeTestHost.CompleteHelloAsync(socket);
        Assert.True(ack.Accepted);

        // Resolve the REAL IPuddingToolExecutionService from DI (Doc79 §7.4 hard
        // requirement — NOT registry.GetTool().ExecuteAsync()).
        var executionService = host.Services.GetRequiredService<IPuddingToolExecutionService>();

        // ── Assertion ①: registry exposes browser_snapshot ──────────────────
        var registry = host.Services.GetRequiredService<IPuddingToolRegistry>();
        var snapshotTool = registry.GetTool("browser_snapshot");
        Assert.NotNull(snapshotTool);
        Assert.Equal("browser_snapshot", snapshotTool!.Descriptor.ToolId);

        // ── Policy that whitelists browser_snapshot ────────────────────────
        var policy = new CapabilityPolicy
        {
            AllowedToolNames = ["browser_snapshot"],
            AllowNetworkAccess = true,
        };

        // ── Execution context with full identity for Origin assertion ④ ────
        const string workspaceId = "ws-74-test";
        const string sessionId = "sess-74-test";
        const string agentInstanceId = "agent-74-test";
        const string configAgentId = "agent-74-config";
        const string conversationId = "conv-74-test";
        const string runId = "run-74-test";
        const string toolCallId = "tc-74-test";
        const string sentinelArg = "SECRET_FILL_VALUE_DO_NOT_LEAK";

        var context = new ToolExecutionContext
        {
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            AgentInstanceId = agentInstanceId,
            ConfigurationAgentInstanceId = configAgentId,
            ExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = conversationId,
                RunId = runId,
                ToolCallId = toolCallId,
                TraceId = null,
            },
        };

        var argsJson = $$"""{"context_id":"ctx-74","page_id":"page-74","sentinel":"{{sentinelArg}}"}""";

        // ── Act: fire through the real execution service ───────────────────
        var executionTask = executionService.ExecuteAsync(
            "browser_snapshot",
            argsJson,
            context,
            policy,
            CancellationToken.None);

        // ── Reply to bridge commands (pattern from existing integration tests) ──
        const long expectedPageVersion = 74;

        // Command 1: ContextGetInfo
        var contextCmd = await ReceiveCommandAsync(socket, BrowserBridgeCommandNames.ContextGetInfo);
        await ReplyAsync(socket, contextCmd, new BrowserContextDescriptor
        {
            ContextId = "ctx-74",
            UserDataDirectory = "C:/fake-browser-74",
            PageCount = 1,
        });

        // Command 2: PageGetInfo
        var pageCmd = await ReceiveCommandAsync(socket, BrowserBridgeCommandNames.PageGetInfo);
        await ReplyAsync(socket, pageCmd, new BrowserPageDescriptor
        {
            ContextId = "ctx-74",
            PageId = "page-74",
            Title = "Test Page 74",
            Url = "https://test74.local/",
            PageVersion = expectedPageVersion,
        });

        // Command 3: PageSnapshot
        var snapshotCmd = await ReceiveCommandAsync(socket, BrowserBridgeCommandNames.PageSnapshot);
        const string fakeDomText = "div ref=v74-n1 name=\"Submit\"";
        const string fakeA11yTree = "button ref=v74-n1 name=\"Submit\"";
        await ReplyAsync(socket, snapshotCmd, new BrowserSnapshotDescriptor
        {
            DomText = fakeDomText,
            AccessibilityTree = fakeA11yTree,
            NodeCount = 1,
            PageVersion = expectedPageVersion,
        });

        var result = await executionTask;

        // ════════════════════════════════════════════════════════════════════
        // Assertion ②: Tool Policy allowed invocation (firewall passed)
        // ════════════════════════════════════════════════════════════════════
        Assert.True(result.Success,
            $"Tool execution should succeed through the firewall. Error: {result.Error}");

        // ════════════════════════════════════════════════════════════════════
        // Assertion ③: Broker received commands (3 bridge commands sent)
        // ════════════════════════════════════════════════════════════════════
        // (Verified implicitly by ReceiveCommandAsync above — each call
        //  asserts the expected command name, and if fewer commands arrived
        //  the test would hang/timeout.)

        // ════════════════════════════════════════════════════════════════════
        // Assertion ④: Command Origin contains correct Agent / Session / Run / ToolCall
        // ════════════════════════════════════════════════════════════════════
        AssertOrigin(contextCmd.Origin, workspaceId, configAgentId, sessionId,
            conversationId, runId, toolCallId, "browser_snapshot");
        AssertOrigin(pageCmd.Origin, workspaceId, configAgentId, sessionId,
            conversationId, runId, toolCallId, "browser_snapshot");
        AssertOrigin(snapshotCmd.Origin, workspaceId, configAgentId, sessionId,
            conversationId, runId, toolCallId, "browser_snapshot");

        // ════════════════════════════════════════════════════════════════════
        // Assertion ⑤: PageVersion and structured result returned to caller
        // ════════════════════════════════════════════════════════════════════
        Assert.True(result.Success);
        using var outputDoc = JsonDocument.Parse(result.Output);
        var root = outputDoc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedPageVersion, root.GetProperty("pageVersion").GetInt64());

        var value = root.GetProperty("value");
        Assert.Equal(1, value.GetProperty("nodeCount").GetInt32());
        Assert.Contains("v74-n1", value.GetProperty("accessibilityTree").GetString(),
            StringComparison.Ordinal);

        // ════════════════════════════════════════════════════════════════════
        // Assertion ⑥: Activity output contains no raw Arguments or DOM sentinel
        // ════════════════════════════════════════════════════════════════════
        var serialized = result.Output;
        // The sentinel injected into arguments MUST NOT appear anywhere
        Assert.DoesNotContain(sentinelArg, serialized, StringComparison.Ordinal);
        // The raw DOM text is returned in the structured "domText" field but the
        // arguments sentinel (which is not a real page attribute) must not leak.
        // Additionally verify the raw args JSON is not blindly echoed:
        Assert.DoesNotContain("SECRET_FILL", serialized, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<BrowserBridgeCommand> ReceiveCommandAsync(
        WebSocket socket, string expectedName)
    {
        var envelope = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        var command = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommand>(envelope);
        Assert.Equal(expectedName, command.Name);
        return command;
    }

    private static Task ReplyAsync(
        WebSocket socket, BrowserBridgeCommand command, object value)
        => BrowserBridgeTestHost.SendEnvelopeAsync(socket, new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = command.OperationId,
            Kind = BrowserBridgeMessageKind.CommandResult,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
                Value = JsonSerializer.SerializeToElement(value, BrowserBridgeTestJson.Options),
            }, BrowserBridgeTestJson.Options),
        });

    private static void AssertOrigin(
        BrowserBridgeCommandOrigin? origin,
        string expectedWorkspaceId,
        string expectedAgentInstanceId,
        string expectedSessionId,
        string expectedConversationId,
        string expectedRunId,
        string expectedToolCallId,
        string expectedToolName)
    {
        Assert.NotNull(origin);
        Assert.Equal(expectedWorkspaceId, origin!.WorkspaceId);
        Assert.Equal(expectedAgentInstanceId, origin.AgentInstanceId);
        Assert.Equal(expectedSessionId, origin.SessionId);
        Assert.Equal(expectedConversationId, origin.ConversationId);
        Assert.Equal(expectedRunId, origin.RunId);
        Assert.Equal(expectedToolCallId, origin.ToolCallId);
        Assert.Equal(expectedToolName, origin.ToolName);
    }
}
