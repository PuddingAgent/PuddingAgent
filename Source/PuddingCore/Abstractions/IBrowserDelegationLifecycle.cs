namespace PuddingCode.Abstractions;

/// <summary>
/// **浏览器委派生命周期窄端口**（方案 §6.2）—— **只用标量**，因此本程序集（`PuddingCore`）
/// **无需引用 `Pudding.Contracts`**。
///
/// 为什么在这里而不是 Contracts：`PuddingPlatform` 的 ProjectReference 只有
/// `PuddingCore` / `PuddingMemoryEngine` / `PuddingFullTextIndex` / `PuddingPathFiltering`
/// ⇒ 它**看不到 Contracts 里的类型**（2026-10-12 实测：把端口声明在 Contracts 会让整链构建
/// `CS0246`）。而 Platform / Runtime / Host **都引用 Core** ⇒ Core 是唯一的公共落点。
///
/// 强类型映射与真实授权判定都在 Host 侧实现（Host 同时引用 Core 与 Contracts）。
///
/// 不变量（实现方必须满足）：
/// <list type="bullet">
/// <item>**父身份由可信执行上下文投影**，绝不接受模型参数里的 caller/runId；</item>
/// <item>子授权绑定**真实子 runId**（不得复用父 runId 或模板 id）；</item>
/// <item>子范围必须是父范围的**子集**，到期取 `min(请求值, 父剩余)`；</item>
/// <item>子任务成功 / 失败 / 取消 / 超时 / 父终止都必须**撤销**（统一收尾）；</item>
/// <item>断连时本地标记撤销，**新世代不得自动恢复旧授权**；</item>
/// <item>被拒时必须给出**可区分的原因**，调用方据此 fail-closed。</item>
/// </list>
/// </summary>
public interface IBrowserDelegationLifecycle
{
    /// <summary>
    /// 为**刚产生的子运行**申请浏览器子集授权。
    /// 解析失败（未知范围线名、目标只给一半）或父授权不匹配 ⇒ **明确拒绝**（不得静默放宽）。
    /// </summary>
    ValueTask<BrowserDelegationGrantResult> RequestChildGrantAsync(
        BrowserDelegationGrantRequest request,
        CancellationToken cancellationToken);

    /// <summary>撤销某个运行的全部浏览器委派授权（成功/失败/取消/超时/父终止都必须调用）。</summary>
    ValueTask RevokeForRunAsync(string runId, string reason, CancellationToken cancellationToken);

    /// <summary>按**完整任务运行身份**撤销（不得误撤同一 Agent 的其它 run）。</summary>
    ValueTask RevokeForTaskAsync(
        string callerKind,
        string callerId,
        string runId,
        string? parentRunId,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>连接丢失：本地标记撤销；新世代**不自动恢复**旧授权。</summary>
    ValueTask OnConnectionLostAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>
/// 子集授权申请（**标量**）：父/子运行身份 + 目标 + 请求范围线名与期限。
/// 范围用**线名**（如 `read`/`write`）而非枚举 ⇒ Core 不必依赖 Contracts；Host 侧严格解析，未知即拒绝。
/// </summary>
public sealed record BrowserDelegationGrantRequest(
    string CallerKind,
    string CallerId,
    string ParentRunId,
    string? ParentTaskId,
    string ChildRunId,
    string ContextId,
    string PageId,
    string ScopeName,
    TimeSpan RequestedValidity);

/// <summary>子集授权结果（**标量**）：被授予的 grant 引用与绝对到期时间；拒绝必须带原因。</summary>
public sealed record BrowserDelegationGrantResult(
    string? GrantId,
    string? GrantedScopeName,
    DateTimeOffset? ExpiresAtUtc,
    string? DenialReason = null)
{
    /// <summary>是否真的拿到授权（拒绝时必须给出 <see cref="DenialReason"/>）。</summary>
    public bool Granted => !string.IsNullOrWhiteSpace(GrantId);
}
