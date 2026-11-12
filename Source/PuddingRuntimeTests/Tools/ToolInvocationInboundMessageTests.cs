using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 入站用户消息级 provenance 的透传契约：ToolInvocationRequest.InboundMessageId 必须原样抵达
/// ToolExecutionContext.InboundMessageId，工具才能把它写进记忆章节（与 ChatMessages.message_id 对齐）。
/// 缺失时保持 null —— 上游没给就不得编造。
/// </summary>
[TestClass]
public sealed class ToolInvocationInboundMessageTests
{
    [TestMethod]
    public async Task ToolInvocationService_Forwards_InboundMessageId_To_ToolContext()
    {
        var tool = new CapturingTool();
        var invocation = BuildInvocation(tool);

        var result = await invocation.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-1",
            ToolName = "capture_inbound",
            ArgumentsJson = "{}",
            CapabilityPolicy = AllowPolicy(),
            InboundMessageId = "m-9",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("m-9", tool.LastInboundMessageId, "工具上下文必须拿到入站用户消息 id");
    }

    [TestMethod]
    public async Task ToolInvocationService_Leaves_InboundMessageId_Null_When_Absent()
    {
        var tool = new CapturingTool();
        var invocation = BuildInvocation(tool);

        var result = await invocation.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-1",
            ToolName = "capture_inbound",
            ArgumentsJson = "{}",
            CapabilityPolicy = AllowPolicy(),
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(tool.LastInboundMessageId, "上游没有消息 id 时不得编造");
    }

    private static ToolInvocationService BuildInvocation(IPuddingTool tool)
    {
        var executor = new PuddingToolExecutionService(
            new PuddingToolRegistry([tool]),
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance);
        return new ToolInvocationService(
            executor,
            workspaceGuard: null,
            NullLogger<ToolInvocationService>.Instance);
    }

    private static CapabilityPolicy AllowPolicy()
        => new() { DefaultToolNames = ["capture_inbound"] };

    private sealed class CapturingTool : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = "capture_inbound",
            Name = "Capture inbound message id",
            Description = "Captures the inbound message id carried by the tool context.",
            Category = ToolCategory.Query,
            PermissionLevel = ToolPermissionLevel.Low,
            Safety = ToolSafetyFlags.ReadOnly,
        };

        public string? LastInboundMessageId { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        {
            LastInboundMessageId = request.Context.InboundMessageId;
            return Task.FromResult(ToolExecutionResult.Ok("captured"));
        }
    }
}
