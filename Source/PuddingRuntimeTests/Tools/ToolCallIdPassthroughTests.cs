using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// T05 P0-A：callId 身份闭环——执行层全链路透传（不可变）+ SSE 帧携带 toolCallId。
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md B:87-93、B:227-254。
/// </summary>
[TestClass]
public sealed class ToolCallIdPassthroughTests
{
    [TestMethod]
    public async Task PuddingToolExecutionService_Preserves_Caller_ToolCallId_From_Context()
    {
        var tool = new CapturingTool();
        var executor = BuildExecutor(tool);

        var result = await executor.ExecuteAsync(
            "capture_tool",
            "{}",
            Context() with { ToolCallId = "call-context" },
            AllowPolicy());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("call-context", tool.LastToolCallId);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Preserves_Caller_ToolCallId_From_ExecutionIdentity()
    {
        var tool = new CapturingTool();
        var executor = BuildExecutor(tool);

        var result = await executor.ExecuteAsync(
            "capture_tool",
            "{}",
            Context() with
            {
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = "session-1",
                    RunId = "run-1",
                    ToolCallId = "call-identity",
                },
            },
            AllowPolicy());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("call-identity", tool.LastToolCallId);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Prefers_Context_ToolCallId_Over_ExecutionIdentity()
    {
        var tool = new CapturingTool();
        var executor = BuildExecutor(tool);

        var result = await executor.ExecuteAsync(
            "capture_tool",
            "{}",
            Context() with
            {
                ToolCallId = "call-context-wins",
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = "session-1",
                    RunId = "run-1",
                    ToolCallId = "call-identity",
                },
            },
            AllowPolicy());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("call-context-wins", tool.LastToolCallId);
    }

    [TestMethod]
    public async Task PuddingToolExecutionService_Synthesizes_Stable_ToolCallId_When_No_Identity()
    {
        var tool = new CapturingTool();
        var executor = BuildExecutor(tool);

        var result = await executor.ExecuteAsync(
            "capture_tool",
            "{}",
            Context(),
            AllowPolicy());

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(tool.LastToolCallId);
        Assert.AreEqual(32, tool.LastToolCallId!.Length);
        Assert.IsTrue(Guid.TryParseExact(tool.LastToolCallId, "N", out _));
    }

    [TestMethod]
    public async Task ToolInvocationService_Preserves_CallId_EndToEnd()
    {
        var tool = new CapturingTool();
        var registry = new PuddingToolRegistry([tool]);
        var executor = new PuddingToolExecutionService(
            registry,
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);
        var invocation = new ToolInvocationService(
            executor,
            workspaceGuard: null,
            NullLogger<ToolInvocationService>.Instance);

        var result = await invocation.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-e2e",
            ToolName = "capture_tool",
            ArgumentsJson = "{}",
            CapabilityPolicy = AllowPolicy(),
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("call-e2e", result.ToolCallId);
        Assert.AreEqual("call-e2e", tool.LastToolCallId);
    }

    [TestMethod]
    public void ToolCallSseFrame_Carries_ToolCallId()
    {
        var frame = ServerSentEventFrame.Json(SseEventTypes.ToolCall,
            new { name = "search_tools", arguments = """{"query":"x"}""", toolCallId = "call-frame" });

        Assert.AreEqual(SseEventTypes.ToolCall, frame.Event);

        using var doc = JsonDocument.Parse(frame.Data);
        Assert.IsTrue(doc.RootElement.TryGetProperty("toolCallId", out var toolCallId));
        Assert.AreEqual("call-frame", toolCallId.GetString());
        Assert.AreEqual("search_tools", doc.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public void ToolResultSseFrame_Carries_ToolCallId()
    {
        var frame = ServerSentEventFrame.Json(SseEventTypes.ToolResult, new
        {
            name = "search_tools",
            toolCallId = "call-frame",
            exitCode = 0,
            output = "ok",
            error = (string?)null,
        });

        Assert.AreEqual(SseEventTypes.ToolResult, frame.Event);

        using var doc = JsonDocument.Parse(frame.Data);
        Assert.IsTrue(doc.RootElement.TryGetProperty("toolCallId", out var toolCallId));
        Assert.AreEqual("call-frame", toolCallId.GetString());
    }

    private static PuddingToolExecutionService BuildExecutor(IPuddingTool tool)
        => new(
            new PuddingToolRegistry([tool]),
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);

    private static CapabilityPolicy AllowPolicy()
        => new() { DefaultToolNames = ["capture_tool"] };

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
    };

    private sealed class CapturingTool : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = "capture_tool",
            Name = "Capture tool",
            Description = "Captures the incoming tool call id.",
            Category = ToolCategory.Query,
            PermissionLevel = ToolPermissionLevel.Low,
            Safety = ToolSafetyFlags.ReadOnly,
        };

        public string? LastToolCallId { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        {
            LastToolCallId = request.ToolCallId;
            return Task.FromResult(ToolExecutionResult.Ok("captured"));
        }
    }
}
