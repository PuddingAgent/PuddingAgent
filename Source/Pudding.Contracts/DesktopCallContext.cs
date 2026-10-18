using Pudding.Contracts.Desktop;

namespace Pudding.Contracts;

/// <summary>
/// 一次 Desktop 能力调用的调用上下文：目标 Desktop、操作 ID、截止时间与业务关联 ID。
///
/// 身份与权限<b>不</b>由模型填写的字段决定；可信运行上下文由 Core 产生并在此传递。
/// WebView 类调用的具体目标（ContextId/PageId）属于请求 DTO，见
/// <c>Pudding.Contracts.Desktop.DesktopPageTarget</c> —— 禁止用「当前激活的 Tab」隐式定位。
/// </summary>
public sealed record DesktopCallContext
{
    /// <summary>
    /// 权限证据（第一阶段：只携带，不在接缝处校验）。
    /// <para>
    /// **不上线缆**：能力通道的 wire 只携带 operation_id / generation / capability / deadline，
    /// Desktop 侧由这些字段重建上下文 ⇒ 本字段在 Desktop 侧恒为 <c>null</c>（这正是设计意图：
    /// 证据留在 Core 进程内，接缝自行校验）。
    /// </para>
    /// </summary>
    public string? PermissionEvidenceSummary { get; init; }

    /// <summary>
    /// 本调用的**可信调用者身份**（下一阶段实施方案 §3.2）。由 Core 从可信执行上下文投影后随请求传来；
    /// **非浏览器**的 Shell 调用可以没有它，但**所有浏览器自动化操作**必须带一个 <b>Agent/SubAgent</b>
    /// 身份且运行字段完整（见 <see cref="TryGetBrowserCaller"/>）。
    /// <para>
    /// 不接受的来源：模型参数、网页内容、审计 Origin、<c>PermissionEvidenceSummary</c> 的字符串。
    /// </para>
    /// </summary>
    public BrowserCallerIdentity? BrowserCaller { get; init; }

    /// <summary>
    /// 宿主内部的**会话票据标记**（I2-c D2）：表示这次浏览器能力调用已经由 Bridge 派发器
    /// 用会话权威完成准入/复检，执行器**不得**再用旧 authority 的 grant/lease 重复裁决。
    ///
    /// <para>
    /// **不上线缆、不来自模型**：能力通道 wire 不携带本字段（见 `Mapping`/`WireMapping` 只映射
    /// operation/generation/capability/deadline），因此只有 Desktop 进程内可信组合根能设置它；
    /// 缺失即按旧路径（能力通道）处理。实现迁移期间禁止把它暴露给工具参数或日志。
    /// </para>
    /// </summary>
    public string? SessionPermitMarker { get; init; }

    public DesktopCallContext(
        DesktopInstanceId desktopId,
        OperationId operationId,
        DateTimeOffset deadlineUtc,
        DesktopCorrelationId? correlationId = null,
        BrowserCallerIdentity? browserCaller = null)
    {
        BrowserCaller = browserCaller;
        DesktopId = desktopId ?? throw new ArgumentNullException(nameof(desktopId));
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));

        if (deadlineUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Deadline must be expressed in UTC (offset 00:00) so that it never depends on the caller's time zone.",
                nameof(deadlineUtc));
        }

        DeadlineUtc = deadlineUtc;
        CorrelationId = correlationId;
    }

    public DesktopInstanceId DesktopId { get; }

    public OperationId OperationId { get; }

    /// <summary>单次操作的截止时刻（UTC）。长连接 deadline 不代替单操作 deadline。</summary>
    public DateTimeOffset DeadlineUtc { get; }

    /// <summary>可选的业务关联 ID（聊天/工具调用），用于审计关联，不参与权限判定。</summary>
    public DesktopCorrelationId? CorrelationId { get; }

    public bool IsExpiredAt(DateTimeOffset utcNow) => utcNow >= DeadlineUtc;

    /// <summary>
    /// **浏览器操作的准入前置**（§3.2）：只有可信的 Agent/SubAgent 且运行字段完整才返回 <c>true</c>。
    /// 其余情况一律给出**具体原因**（调用方据此拒绝，不得回退到默认身份或"当作 User"）。
    /// </summary>
    public bool TryGetBrowserCaller(out BrowserCallerIdentity caller, out string reason)
        => BrowserCallerRequirement.TryRequireCaller(BrowserCaller, out caller, out reason);

    /// <summary>剩余时长；已过期返回负值，调用方需显式判断。</summary>
    public TimeSpan RemainingAt(DateTimeOffset utcNow) => DeadlineUtc - utcNow;

    public override string ToString() =>
        $"{DesktopId.Value}/{OperationId.Value}@{DeadlineUtc:O}";
}
