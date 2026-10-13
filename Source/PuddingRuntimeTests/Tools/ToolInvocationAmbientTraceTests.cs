using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 能力门禁「调用方身份」契约：**工具执行期间，本次调用的运行轨迹必须被发布到 Ambient（AsyncLocal）**。
///
/// 背景（2026-10-13 根因定位）：`RuntimeTraceContextCallerIdentityProvider` 只能从 Ambient 取身份，
/// 而 Ambient 的唯一写入者是事件分发链（会话级、不含 Agent 身份）；Agent 执行侧构建的轨迹
/// （携带 AgentInstanceId / ExecutionId）此前**只作为数据向下传、从未压栈**
/// ⇒ `TrustedCallerDesktopCapabilityAuthorizer` 规则② 对 Agent 发起的工具调用永远拒绝
/// （表现为浏览器能力对 Agent 恒不可用）。
///
/// 本组用例钉住的不变式：
/// ① 工具执行边界必须发布 `request.Trace`（含 Agent 身份），供能力门禁读取；
/// ② 请求未携带轨迹时**不得**清空/替换上层 Ambient（否则会抹掉上层已有身份）。
///
/// 诚实标注：未写「退出后恢复上层轨迹」用例 —— AsyncLocal 写入不会向调用方上下文回流，
/// 该断言在同一方法内恒真、属**空测试**；恢复语义由 `using` 作用域静态保证。
/// </summary>
[TestClass]
public sealed class ToolInvocationAmbientTraceTests
{
    private const string ToolId = "ambient_capture_tool";

    [TestMethod]
    public async Task ToolExecution_Publishes_InvocationTrace_WithAgentIdentity_ToAmbient()
    {
        var tool = new AmbientCapturingTool();
        var invocation = BuildInvocation(tool);
        var executionTrace = RuntimeTraceContext
            .CreateNew(sessionId: "session-1", workspaceId: "workspace-1")
            .WithAgent("agent-1", "template-1");

        var result = await invocation.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-ambient",
            ToolName = ToolId,
            ArgumentsJson = "{}",
            CapabilityPolicy = AllowPolicy(),
            Trace = executionTrace,
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(
            tool.ObservedTrace,
            "工具执行期间 Ambient 轨迹为空 —— 能力门禁将取不到任何调用方身份。");
        Assert.AreEqual(executionTrace.TraceId, tool.ObservedTrace!.TraceId);
        Assert.AreEqual(
            "agent-1",
            tool.ObservedTrace.AgentInstanceId,
            "工具执行期间 Ambient 轨迹缺少 AgentInstanceId —— 授权器规则②会拒绝（浏览器能力不可用）。");
        Assert.AreEqual("session-1", tool.ObservedTrace.SessionId);
    }

    [TestMethod]
    public async Task ToolExecution_WithoutTrace_Leaves_Upper_Ambient_Untouched()
    {
        var tool = new AmbientCapturingTool();
        var invocation = BuildInvocation(tool);
        var outer = RuntimeTraceContext.CreateNew(sessionId: "outer-session");

        using var outerScope = RuntimeTraceContextAccessor.Scope(outer);

        var result = await invocation.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-ambient-null",
            ToolName = ToolId,
            ArgumentsJson = "{}",
            CapabilityPolicy = AllowPolicy(),
            Trace = null,
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreSame(
            outer,
            tool.ObservedTrace,
            "请求未携带轨迹时不得清空或替换上层 Ambient（否则会抹掉上层已有身份）。");
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

    private static CapabilityPolicy AllowPolicy() => new() { DefaultToolNames = [ToolId] };

    private sealed class AmbientCapturingTool : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = ToolId,
            Name = "Ambient capture tool",
            Description = "Captures the ambient runtime trace observed during execution.",
            Category = ToolCategory.Query,
            PermissionLevel = ToolPermissionLevel.Low,
            Safety = ToolSafetyFlags.ReadOnly,
        };

        public RuntimeTraceContext? ObservedTrace { get; private set; }

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken ct = default)
        {
            // 穿过一次 await：真实工具（如浏览器工具）会在更深层的异步调用里读取 Ambient。
            await Task.Yield();
            ObservedTrace = RuntimeTraceContextAccessor.Current;
            return ToolExecutionResult.Ok("captured");
        }
    }
}
