namespace PuddingCode.Goals;

/// <summary>
/// 「Agent 自主恢复 Goal」（goal resume 端口）的跨层契约。
/// <para>
/// 与 <see cref="PuddingCode.Tasks.ITaskGoalLaunchService"/> 同模式：接口定义在 PuddingCore，
/// 由 PuddingPlatform 的 <c>GoalResumeService</c> 实现；PuddingRuntime 的 goal_resume 工具
/// （后续波次）只透传 wire 值，不依赖 Platform 类型 —— PuddingRuntime.csproj 的
/// ProjectReference 不含 PuddingPlatform，Runtime 工具层只能经 Core 端口访问平台能力。
/// </para>
/// <para>
/// 服务语义（全部 fail-closed）：复用 canonical resume 路径
/// （<see cref="IGoalCommandService"/> → GoalCommandService.HandleResumeAsync），
/// 不复制状态机判断；在委托前新增三道只读前置闸门 ——
/// ① 归属校验（goal.held_by_other_agent）；
/// ② 熔断证据闸门（blocked_code=no_progress_circuit_open 时 EvidenceRefs 必填，
///    否则 goal.circuit_evidence_required）；
/// ③ 同一 activation epoch 内自动恢复上限 1 次（goal.resume_epoch_limit）。
/// 校验失败路径不产生任何写入。人工 slash / HTTP resume 不经本端口，不受上述自动恢复约束。
/// </para>
/// </summary>
public interface IGoalResumeService
{
    /// <summary>按 定位 → 归属 → 幂等 → 熔断证据 → epoch 配额 → 委托 canonical resume 的
    /// 顺序执行，返回结构化结果（拒绝码见 <see cref="GoalResumeCodes"/>）。</summary>
    Task<GoalResumeResult> ResumeAsync(GoalResumeRequest request, CancellationToken ct = default);
}

/// <summary>goal resume 请求。身份与作用域由工具层从运行时上下文填充
/// （<c>ToolExecutionContext.WorkspaceId</c> / <c>ToolExecutionContext.AgentInstanceId</c> /
/// SessionId 映射的会话 ID），不接受调用方伪造。</summary>
public sealed record GoalResumeRequest
{
    /// <summary>工作区 ID（工具层取 ToolExecutionContext.WorkspaceId）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Goal 续跑会话 ID（工具层从运行时会话上下文填充）。</summary>
    public required string ConversationId { get; init; }

    /// <summary>发起者 Agent 身份（工具层取 ToolExecutionContext.AgentInstanceId）。
    /// 用于归属校验（goal.agent_instance_id 必须一致）与审计。</summary>
    public required string AgentInstanceId { get; init; }

    /// <summary>
    /// 可选：目标 GoalRunId。缺省时定位「当前会话 + 本人归属」的非终态 Goal
    /// （GoalRunStore.FindActiveAsync）；无则回退会话最新 Goal（FindLatestAsync，
    /// 含终态 —— 终态在后续闸门被 goal.not_resumable 精确拒绝）。
    /// 提供 GoalRunId 时，goal 不存在或其 CurrentConversationId 与本请求会话不一致
    /// 一律返回 goal.not_found（不泄露跨会话 goal 的存在性，fail-closed）。
    /// </summary>
    public string? GoalRunId { get; init; }

    /// <summary>可选 CAS：不符 ⇒ <see cref="GoalResumeCodes.VersionConflict"/>。
    /// CAS 比对由 canonical resume 路径（HandleResumeAsync）执行，本端口不复制。</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>可选恢复原因（进入 GoalCommand.Reason 与审计日志，便于事件溯源）。</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 熔断恢复证据引用（如人工介入记录、阻塞解除依据的 artifact/事件引用）。
    /// goal 处于 <see cref="GoalResumeCodes.CircuitBlockerCode"/> 熔断阻塞时必填非空，
    /// 否则 goal.circuit_evidence_required。
    /// </summary>
    public IReadOnlyList<string>? EvidenceRefs { get; init; }
}

/// <summary>goal resume 结果。<para>
/// 字段语义：<see cref="Success"/>=false 表示拒绝（Code 为拒绝码）；
/// <see cref="Success"/>=true 且 <see cref="Resumed"/>=false 表示幂等命中
/// （goal.already_active，本次调用未产生状态转换）；<see cref="Resumed"/>=true 表示
/// 本次调用完成了 paused/blocked → active 的实际转换。
/// 快照关键字段在拒绝路径也尽量回填当前事实，供工具层透传给调用方。</para></summary>
public sealed record GoalResumeResult
{
    /// <summary>false ⇒ 拒绝；幂等 already_active 视为成功。</summary>
    public required bool Success { get; init; }

    /// <summary>true ⇒ 本次调用完成了 paused/blocked → active 的实际转换。</summary>
    public required bool Resumed { get; init; }

    /// <summary>稳定 wire 值，见 <see cref="GoalResumeCodes"/>。</summary>
    public required string Code { get; init; }

    /// <summary>人类可读说明（中文；含拒绝原因或恢复结果语义补充）。</summary>
    public string? Message { get; init; }

    // ── 状态快照关键字段（来自服务端 goal 投影，拒绝路径也回填当前事实）────────
    public string? GoalRunId { get; init; }

    public GoalPhase? Phase { get; init; }

    public string? BlockedCode { get; init; }

    public int? ActivationEpoch { get; init; }

    public int? AggregateVersion { get; init; }

    public int? IterationsStarted { get; init; }

    public int? IterationsSettled { get; init; }

    public int? MaxIterations { get; init; }
}

/// <summary>goal resume 稳定 wire 码（Runtime 工具层直接透传给调用方，不得重命名）。</summary>
public static class GoalResumeCodes
{
    /// <summary>成功：本次调用完成了 paused/blocked → active 转换。</summary>
    public const string Resumed = "goal.resumed";

    /// <summary>幂等成功：Goal 已处于 active（Success=true，Resumed=false，无任何写入）。</summary>
    public const string AlreadyActive = "goal.already_active";

    /// <summary>Goal 不存在，或不属于当前会话/本人可见范围（按 GoalRunId 定位时，
    /// 跨会话 goal 一律折叠为本码，不泄露存在性）。</summary>
    public const string NotFound = "goal.not_found";

    /// <summary>终态 Goal（completed/cancelled/failed/budget_exhausted）不可恢复
    /// （GoalStateMachine.CanResume 仅放行 paused/blocked）。</summary>
    public const string NotResumable = "goal.not_resumable";

    /// <summary>目标 Goal 归属其他 Agent（goal.agent_instance_id 与请求身份不一致），
    /// fail-closed 拒绝，无论 GoalRunId 是否显式提供。</summary>
    public const string HeldByOtherAgent = "goal.held_by_other_agent";

    /// <summary>ExpectedVersion CAS 不符（由 canonical resume 路径裁决）。</summary>
    public const string VersionConflict = "goal.version_conflict";

    /// <summary>熔断闸门：goal 因无进展熔断而阻塞
    /// （blocked_code=no_progress_circuit_open）时，自动恢复必须携带非空 EvidenceRefs。</summary>
    public const string CircuitEvidenceRequired = "goal.circuit_evidence_required";

    /// <summary>epoch 配额：同一 goalRun 在同一 activation epoch 内自动恢复上限 1 次；
    /// 任何后续状态转换（pause/blocked/人工介入/重启换发 fence）都会推进 epoch 并重置配额。</summary>
    public const string ResumeEpochLimit = "goal.resume_epoch_limit";

    /// <summary>
    /// 无进展熔断阻塞码字面量。唯一权威定义在 GoalSettlementStore.cs:1387
    /// （private const NoProgressCircuitOpenBlockerCode）；Core 无公共常量，
    /// 此处按值对齐并注明来源锚点 —— 两侧必须同值，结算侧改名时需同步本常量。
    /// </summary>
    public const string CircuitBlockerCode = "no_progress_circuit_open";
}
