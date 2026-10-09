using Pudding.Contracts.Desktop;
using PuddingCode.Runtime;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// **把 Core 的执行上下文投影成调用者身份**（设计 §3.1/§3.2：身份来自 Core 的**可信执行上下文**，
/// 不让模型填写 owner/epoch 来获得权限）。
///
/// 这是工具层唯一允许产生 <see cref="BrowserCallerIdentity"/> 的地方，入参**只有两样**：
/// <list type="number">
///   <item><c>RuntimeExecutionIdentity</c>（编排层创建、Runtime 与工具只能透传 —— 见其类型注释）；</item>
///   <item>agent 实例标识（与 <c>BrowserOperationOrigin</c> 用同一条规则取值，保证"审计里的行动者"与"授权里的行动者"一致）。</item>
/// </list>
/// **没有**任何工具参数参与 ⇒ 模型无法自报身份。
///
/// 子代理判定直接取自 <see cref="RuntimeExecutionKind.SubAgent"/>（而不是"有没有 ParentRunId"这种可伪造的推断）。
/// </summary>
public static class BrowserCallerIdentitySource
{
    /// <summary>
    /// 尝试投影；失败时给出**具体原因**（调用方必须如实上报，不得改用默认身份继续）。
    /// </summary>
    public static bool TryProject(
        RuntimeExecutionIdentity? identity,
        string? agentInstanceId,
        out BrowserCallerIdentity? caller,
        out string reason)
    {
        if (identity is null)
        {
            // 没有执行上下文 = 没有可信身份来源。**不**回退到任何环境推断。
            caller = null;
            reason = "no trusted execution identity is available; browser actions are not admitted without one";
            return false;
        }

        return BrowserCallerIdentityProjection.TryProject(
            // 子代理判定来自**编排层声明的类型**，不是"有没有父 RunId"（后者可被伪造/遗漏）。
            isSubAgent: identity.Kind == RuntimeExecutionKind.SubAgent,
            callerId: agentInstanceId,
            runId: identity.RunId,
            parentRunId: identity.ParentRunId,
            out caller,
            out reason);
    }
}
