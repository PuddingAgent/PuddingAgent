namespace PuddingCode.Tasks;

/// <summary>
/// Active Task Runtime Context — 由派发链注入，随本次 Agent Run 存活。
/// <para>
/// 权威来源：ADR-072 §9.1 Envelope metadata + §9.2 注入字段。Tool 不得自行解析 Transcript；
/// task_id/assignment_id/expected_version 由 Runtime 注入，模型不得伪造（工具执行时以 Context 为准，
/// 忽略参数中与之冲突的值）。
/// </para>
/// </summary>
public sealed record ActiveTaskRuntimeContext
{
    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>Assignment ID。</summary>
    public required string AssignmentId { get; init; }

    /// <summary>被分配 Agent ID。</summary>
    public required string AgentId { get; init; }

    /// <summary>wire: task.manual | task.auto | automation.schedule（ADR-072 §9.1）。</summary>
    public required string Origin { get; init; }

    /// <summary>wire: p0 | p1 | p2 | p3。</summary>
    public required string Priority { get; init; }

    /// <summary>wire: inherit | anytime | off_peak_only。</summary>
    public required string ExecutionWindow { get; init; }

    /// <summary>派发时刻的 Task.Version（审计与迟到校验基线，见 §4.3）。</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>派发时的 policy 版本（ADR-072 §9.1 metadata.policy_version）。</summary>
    public string? PolicyVersion { get; init; }

    /// <summary>Outbox 幂等键（ADR-072 §9.1 metadata.dispatch_idempotency_key）。</summary>
    public string? DispatchIdempotencyKey { get; init; }

    /// <summary>Delivery ID（TB-05 binding 已绑定；可用于 task_update 关联）。</summary>
    public string? DeliveryId { get; init; }

    /// <summary>单调 Fencing Token（AU-02 Reservation 引入；第一阶段恒 null）。</summary>
    public string? ReservationFencingToken { get; init; }
}

/// <summary>
/// 任务看板 Feature Flag（合同冻结 v1 §3）。
/// <para>
/// <c>Enabled</c> 关闭时，四个 task_* 工具返回 <c>capability.missing</c>（403 语义）。
/// 其余 Flag 与四工具无直接耦合（claim 的 Fence 检查点 stub 阶段恒 allow）。
/// </para>
/// </summary>
public sealed class WorkspaceTaskFeatureOptions
{
    /// <summary>任务看板总开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>自动派发（P1）。</summary>
    public bool AutoDispatchEnabled { get; set; }

    /// <summary>定时任务（P1）。</summary>
    public bool AutomationEnabled { get; set; }

    /// <summary>峰谷 Fence（P1）。</summary>
    public bool PeakFenceEnabled { get; set; }
}
