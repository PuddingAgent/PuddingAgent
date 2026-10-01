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
    public DesktopCallContext(
        DesktopInstanceId desktopId,
        OperationId operationId,
        DateTimeOffset deadlineUtc,
        DesktopCorrelationId? correlationId = null)
    {
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

    /// <summary>剩余时长；已过期返回负值，调用方需显式判断。</summary>
    public TimeSpan RemainingAt(DateTimeOffset utcNow) => DeadlineUtc - utcNow;

    public override string ToString() =>
        $"{DesktopId.Value}/{OperationId.Value}@{DeadlineUtc:O}";
}
