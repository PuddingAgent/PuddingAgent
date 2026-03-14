namespace PuddingCode.Models;

/// <summary>蜂群任务定义。</summary>
public sealed record SwarmTask
{
    /// <summary>任务唯一标识符。</summary>
    public required string Id { get; init; }

    /// <summary>任务标题。</summary>
    public required string Title { get; init; }

    /// <summary>任务描述。</summary>
    public required string Description { get; init; }

    /// <summary>关联的契约 ID。</summary>
    public string? ContractId { get; init; }

    /// <summary>Worker 被允许修改的作用域。</summary>
    public WorkerScope? Scope { get; init; }

    /// <summary>分配给的 Worker ID。</summary>
    public string? AssignedTo { get; set; }

    /// <summary>任务状态。</summary>
    public SwarmTaskStatus Status { get; set; } = SwarmTaskStatus.Created;

    /// <summary>任务执行结果。</summary>
    public string? Result { get; set; }

    /// <summary>任务失败原因（如果失败）。</summary>
    public string? FailReason { get; set; }
}
