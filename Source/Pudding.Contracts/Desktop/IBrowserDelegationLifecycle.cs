namespace Pudding.Contracts.Desktop;

/// <summary>
/// **浏览器委派生命周期窄端口**（方案 §6.2）：由 Runtime/Platform 在**子任务运行真正产生身份之后**调用，
/// 向 Host 的绑定协调器申请/撤销**子集授权**。
///
/// 为什么放在契约层而不是 Host：Runtime 与 Platform **不得引用 Host 实现或 Desktop**；
/// 本接口只使用 Contracts 类型，因此三者都能引用它，而实现（绑定协调器）留在 Host。
///
/// 不变量：
/// <list type="bullet">
/// <item>**父身份由可信执行上下文投影**，绝不接受模型参数里的 caller/runId；</item>
/// <item>子授权必须绑定**真实子 runId**（不得复用父 runId 或模板 id）；</item>
/// <item>子 scope 必须是父 scope 的**子集**，到期时间取 `min(请求值, 父剩余)`；</item>
/// <item>子任务成功/失败/取消/超时/父终止都必须走**统一 finally 撤销**；</item>
/// <item>断连时本地标记撤销，**新世代不得自动恢复旧授权**。</item>
/// </list>
/// </summary>
public interface IBrowserDelegationLifecycle
{
    /// <summary>
    /// 为**刚产生的子运行**申请浏览器子集授权。实现方须校验：父 grant 的完整身份（caller/run、实例/连接、
    /// 目标/frame）、scope 子集、绝对到期时间；父不存在或不匹配 ⇒ **明确拒绝**（不得静默放宽）。
    /// </summary>
    ValueTask<BrowserDelegationGrant> RequestChildGrantAsync(
        BrowserDelegationRequest request,
        CancellationToken cancellationToken);

    /// <summary>撤销某个运行的**全部**浏览器委派授权（任务结束/父撤销/子任务收尾）。</summary>
    ValueTask RevokeForRunAsync(string runId, string reason, CancellationToken cancellationToken);

    /// <summary>按**完整任务运行身份**撤销（不得误撤同一 Agent 的其它 run）。</summary>
    ValueTask RevokeForTaskAsync(
        BrowserCallerIdentity task,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>连接丢失：本地标记撤销，禁止在新世代自动恢复旧授权。</summary>
    ValueTask OnConnectionLostAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>子集授权申请：父/子 run 身份 + 目标 + 请求范围与期限。</summary>
public sealed record BrowserDelegationRequest(
    BrowserCallerIdentity Parent,
    string ChildRunId,
    DesktopPageTarget Target,
    BrowserPageGrantScope Scope,
    TimeSpan RequestedValidity);

/// <summary>子集授权结果：被授予的 grant 引用与绝对到期时间（无授权时为 <c>null</c>）。</summary>
public sealed record BrowserDelegationGrant(
    BrowserPageGrantId? GrantId,
    BrowserPageGrantScope GrantedScope,
    DateTimeOffset? ExpiresAtUtc,
    string? DenialReason = null)
{
    /// <summary>是否真的拿到了授权（拒绝时必须给出 <see cref="DenialReason"/>）。</summary>
    public bool Granted => GrantId is not null;
}
