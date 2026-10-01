namespace Pudding.Contracts.Audit;

/// <summary>单次能力调用的终态分类（审计用，不含业务细节）。</summary>
public enum DesktopCapabilityOutcome
{
    Succeeded,

    /// <summary>执行失败并返回了领域错误。</summary>
    Failed,

    /// <summary>在进入执行前被拒绝：未协商、越权、非法目标、重复 ID 冲突、队列拒绝。</summary>
    Rejected,

    Cancelled,

    DeadlineExceeded,

    /// <summary>连接断开导致 pending 结束（区分「未执行」与「可能已执行」由 ErrorCode 表达）。</summary>
    Disconnected,
}

/// <summary>
/// 审计记录（计划 §7）。
///
/// <b>结构性保证</b>：本类型刻意没有脚本正文、URL、剪贴板内容、Token、页面数据字段，
/// 因此「日志与诊断包不泄密」不依赖实现纪律，而是类型形状本身；
/// 契约测试用反射断言这一点。
/// </summary>
public sealed record DesktopCapabilityAuditRecord
{
    public required OperationId OperationId { get; init; }

    public required ConnectionGeneration Generation { get; init; }

    /// <summary>能力线名（<see cref="DesktopCapabilities"/> 目录值）。</summary>
    public required string Capability { get; init; }

    public required DesktopCapabilityOutcome Outcome { get; init; }

    /// <summary>在队列中等待的时长（含在途上限等待）。</summary>
    public TimeSpan QueueDuration { get; init; }

    /// <summary>执行时长。</summary>
    public TimeSpan ExecutionDuration { get; init; }

    public DesktopCapabilityErrorCode? ErrorCode { get; init; }

    /// <summary>分布式追踪 ID（由 Core 传入，仅用于关联，不含业务数据）。</summary>
    public string? TraceId { get; init; }

    public DesktopCorrelationId? CorrelationId { get; init; }

    public DateTimeOffset RecordedUtc { get; init; }

    public override string ToString() =>
        $"{Capability} op={OperationId.Value} gen={Generation.Value} outcome={Outcome} err={ErrorCode}";
}

/// <summary>审计出口。实现方负责落盘/上报；<b>不得</b>因为审计失败影响调用结果。</summary>
public interface IDesktopCapabilityAuditSink
{
    void Record(DesktopCapabilityAuditRecord record);
}

/// <summary>默认审计实现：不记录任何内容（测试与未配置审计的宿主）。</summary>
public sealed class NullDesktopCapabilityAuditSink : IDesktopCapabilityAuditSink
{
    public static readonly NullDesktopCapabilityAuditSink Instance = new();

    private NullDesktopCapabilityAuditSink()
    {
    }

    public void Record(DesktopCapabilityAuditRecord record)
    {
    }
}
