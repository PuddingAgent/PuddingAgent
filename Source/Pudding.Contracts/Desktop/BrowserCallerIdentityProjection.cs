namespace Pudding.Contracts.Desktop;

/// <summary>
/// **可信调用身份的投影**（设计 §3.1/§3.2）：
/// 「代理身份来自 Core 的**可信执行上下文**」，「不让模型填写 epoch 或 owner 来获得权限」。
///
/// 因此这里是唯一允许产生 <see cref="BrowserCallerIdentity"/> 的地方，且它的入参**只能**是执行上下文里的值
/// （是否子代理、调用者、本次运行、父运行）——**不接收**任何工具参数，也不从页面/模型输入推导身份。
///
/// fail-closed 规则（缺一即拒绝，绝不"给个默认身份"）：
/// <list type="bullet">
///   <item>没有运行身份（<paramref name="runId"/> 空）⇒ 拒绝：无法把授权绑定到一次具体任务；</item>
///   <item>没有调用者（<paramref name="callerId"/> 空）⇒ 拒绝：无法区分是谁在操作；</item>
///   <item>子代理**必须有父任务**（<paramref name="parentRunId"/>）⇒ 否则无法保证"子代理只取父授权的子集"（§3.2）。</item>
/// </list>
/// </summary>
public static class BrowserCallerIdentityProjection
{
    /// <summary>
    /// 把可信执行上下文投影成调用者身份。失败时给出**具体原因**（调用方必须如实上报，不得改用默认身份）。
    /// </summary>
    public static bool TryProject(
        bool isSubAgent,
        string? callerId,
        string? runId,
        string? parentRunId,
        out BrowserCallerIdentity? identity,
        out string reason)
    {
        identity = null;

        if (string.IsNullOrWhiteSpace(callerId))
        {
            reason = "no trusted caller identity in the execution context (browser actions are not admitted without one)";
            return false;
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            reason = "no trusted run identity in the execution context: the authorization cannot be bound to a task";
            return false;
        }

        if (isSubAgent)
        {
            if (string.IsNullOrWhiteSpace(parentRunId))
            {
                // §3.2：子代理只能取得父任务授权的**子集** ⇒ 没有父任务就无法做子集校验。
                reason = "a sub-agent caller must carry its parent task; without it the scope cannot be checked as a subset";
                return false;
            }

            identity = new BrowserCallerIdentity(
                BrowserCallerKind.SubAgent,
                callerId.Trim(),
                runId.Trim(),
                parentRunId.Trim());
            reason = string.Empty;
            return true;
        }

        identity = new BrowserCallerIdentity(BrowserCallerKind.Agent, callerId.Trim(), runId.Trim());
        reason = string.Empty;
        return true;
    }
}
