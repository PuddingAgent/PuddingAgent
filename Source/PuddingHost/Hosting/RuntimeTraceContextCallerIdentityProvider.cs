using Pudding.CapabilityBroker;
using PuddingCode.Observability;

namespace PuddingHost.Hosting;

/// <summary>
/// 从 Core 的**可信运行上下文**取能力调用方身份。
///
/// Runtime 在执行 Agent / 工具时用 `RuntimeTraceContextAccessor`（AsyncLocal）压栈；
/// 模型输出、工具参数与网页字段都改不到它，因此这才是"谁在调用"的可信来源（计划 §7）。
///
/// 没有会话标识时返回 <c>null</c>：授权器据此 **fail closed**（后台任务或不在此上下文里的
/// 调用一律拒绝，而不是猜一个身份）。
/// </summary>
internal sealed class RuntimeTraceContextCallerIdentityProvider : IDesktopCallerIdentityProvider
{
    public DesktopCallerIdentity? TryGetCurrent()
    {
        var trace = RuntimeTraceContextAccessor.Current;
        if (trace is null || string.IsNullOrWhiteSpace(trace.SessionId))
        {
            return null;
        }

        return new DesktopCallerIdentity
        {
            SessionId = trace.SessionId,
            AgentInstanceId = trace.AgentInstanceId,
            ExecutionId = trace.ExecutionId,
            UserId = trace.UserId,
            WorkspaceId = trace.WorkspaceId,
            SubAgentId = trace.SubAgentId,
        };
    }
}
