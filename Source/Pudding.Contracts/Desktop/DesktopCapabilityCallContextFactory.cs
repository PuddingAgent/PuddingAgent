namespace Pudding.Contracts.Desktop;

/// <summary>
/// 为**一次能力调用**产生 <see cref="DesktopCallContext"/>。
///
/// 为什么必须有这个端口：窄端口上每个方法都要求 <see cref="DesktopCallContext"/>
/// （Desktop 实例 ID + 操作 ID + 期限），而工具侧只有业务参数（ContextId/PageId/定位符…）。
/// 这三个字段都**不能由调用方猜**：
/// · Desktop 实例 ID 来自握手协商，只有 Core 侧的活动会话知道；
/// · 操作 ID 必须每次调用新生成（同 ID 不同 payload 会被 broker 判为重复请求而拒绝）；
/// · 期限来自配置/调用方预算。
///
/// 注意这里**不产生调用方身份**：身份由 Core 的可信运行上下文提供，授权器据此判定（计划 §7）。
/// </summary>
public interface IDesktopCapabilityCallContextFactory
{
    /// <summary>
    /// 产生一次调用的上下文。**没有已连接的 Desktop 时返回 <c>null</c>**——
    /// 调用方据此明确失败，而不是猜一个实例 ID 去发一条注定被拒的命令。
    /// </summary>
    /// <param name="timeout">本次调用的期限；<c>null</c> 表示用实现方的默认值。</param>
    DesktopCallContext? TryCreate(TimeSpan? timeout = null);

    /// <summary>
    /// 同上，并**携带本次调用的权限证据摘要**（权限证据链第一阶段：只携带，不校验）。
    /// <para>
    /// 摘要是字符串而非结构化类型，因为本程序集不能引用 <c>PuddingCore</c>（边界由编译期强制）；
    /// 结构化真源在 Core 侧。证据**不上线缆**：Desktop 侧由 operation_id/generation/deadline 重建上下文，
    /// 因此该摘要只存在于 Core 进程内。
    /// </para>
    /// </summary>
    /// <param name="permissionEvidenceSummary">权限证据摘要；<c>null</c> = 未评估（合法状态，不得伪造成“已拒绝”）。</param>
    /// <param name="timeout">本次调用的期限；<c>null</c> 表示用实现方的默认值。</param>
    DesktopCallContext? TryCreate(string? permissionEvidenceSummary, TimeSpan? timeout = null);
}
