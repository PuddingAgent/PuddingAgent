namespace PuddingCode.Tasks;

/// <summary>
/// 「Agent 自主进入 Goal 模式」(<c>task_goal_start</c>) 的跨层契约
/// （设计 <c>Docs/Features/Agent自主进入Goal模式工具设计-2026-09-16.md</c> §3/§4/§5）。
/// <para>
/// 与 <see cref="ITodoStore"/> / <see cref="IWorkspaceTaskAdminService"/> 同模式：接口定义在
/// PuddingCore，由 PuddingPlatform 的 <c>TaskGoalLaunchService</c> 实现；PuddingRuntime 的
/// task_goal_start 工具（后续波次）只透传 wire 值，不依赖 Platform 类型 ——
/// <c>ITaskAutoDispatchStarter</c> 是 Platform 类型，Runtime 工具层不可见，这正是必须新开
/// Core 端口的原因（设计 §3 分层纪律）。
/// </para>
/// <para>
/// 服务语义（设计 §2/§5，全部 fail-closed）：不绕过 admin 授权、不复制围栏逻辑、只复用
/// canonical 调度链（Evaluator → Starter → TransactionStore）；任一前置校验不通过 ⇒
/// 不创建 Goal；同一 task 已有活跃任务绑定 Goal ⇒ 幂等返回既有 GoalRunId；
/// 只派当前这一张卡（maxStartsOverride: 1），绝不触发全量扫描。
/// </para>
/// </summary>
public interface ITaskGoalLaunchService
{
    /// <summary>按设计 §5 校验顺序执行前置校验 → 幂等 → 开关 → 评估 → 派发，返回结构化结果。
    /// 校验失败路径不产生任何写入（不置开关、不评估、不派发）。</summary>
    Task<TaskGoalLaunchResult> LaunchAsync(TaskGoalLaunchRequest request, CancellationToken ct = default);
}

/// <summary>task_goal_start 启动请求（设计 §4）。身份与作用域由工具层从运行时上下文填充
/// （<c>context.WorkspaceId</c> / <c>context.AgentInstanceId</c> / <c>context.SessionId</c>），
/// 不接受调用方伪造。</summary>
public sealed record TaskGoalLaunchRequest
{
    /// <summary>工作区 ID（工具层取 ToolExecutionContext.WorkspaceId）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>目标看板卡 ID（必填）。</summary>
    public required string TaskId { get; init; }

    /// <summary>发起者 Agent 身份（工具层取 ToolExecutionContext.AgentInstanceId）。
    /// 用于任务级授权（§5-4/§5-5）与审计（UpdatedBy）。</summary>
    public required string AgentId { get; init; }

    /// <summary>Goal 续跑会话（工具层取 ToolExecutionContext.SessionId）。
    /// Starter 需要 ConversationId，而 ActiveTaskRuntimeContext 本身不含该值（设计 §4 取证）。</summary>
    public required string ConversationId { get; init; }

    /// <summary>可选 CAS：不符 ⇒ <see cref="TaskGoalLaunchCodes.VersionConflict"/>（§5-2）。</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>可选迭代预算请求：服务端取 min(请求, TaskBoundGoals:GoalIterationBudget)，
    /// 不得抬高（设计 §2-6）。提供时必须 ≥ 1。</summary>
    public int? IterationBudget { get; init; }

    /// <summary>可选原因（进入结果 Message 与日志，便于事件溯源追踪）。</summary>
    public string? Reason { get; init; }
}

/// <summary>task_goal_start 结果（设计 §4 wire 返回体）。
/// <para>
/// 字段来源说明：<see cref="GoalRunId"/> / <see cref="AssignmentId"/> 透传
/// <c>TaskAutoDispatchStartOutcome</c>（TaskAutoDispatchStarter.cs:9-17）；
/// <see cref="ReservationId"/> / <see cref="TaskPlanId"/> 由 TransactionStore 在事务内部产生、
/// 不经 StartOutcome 透出，启动路径恒为 null（诚实留空，不伪造）；幂等路径填充既有
/// binding 的 GoalRunId/AssignmentId（task_goal_bindings 行）。
/// </para></summary>
public sealed record TaskGoalLaunchResult
{
    /// <summary>true ⇒ Goal 已启动（<see cref="TaskGoalLaunchCodes.Started"/>）或幂等命中
    /// 既有运行（<see cref="TaskGoalLaunchCodes.AlreadyRunning"/>）。</summary>
    public required bool Started { get; init; }

    /// <summary>见 <see cref="TaskGoalLaunchCodes"/>；评估/派发拒绝码透传
    /// TaskAutoDispatchCandidateDecision.Code / TaskAutoDispatchStartOutcome.Code（§5-9/§5-10）。</summary>
    public required string Code { get; init; }

    public string? GoalRunId { get; init; }

    public string? AssignmentId { get; init; }

    /// <summary>启动路径不可得（StartOutcome 不透出），恒为 null。</summary>
    public string? ReservationId { get; init; }

    /// <summary>启动路径不可得（StartOutcome 不透出），恒为 null。</summary>
    public string? TaskPlanId { get; init; }

    /// <summary>服务端已知的目标卡版本（预检读取值或评估决策值）。</summary>
    public int? TaskVersion { get; init; }

    /// <summary>人类可读说明（中文；含预算 clamp 结果等语义补充）。</summary>
    public required string Message { get; init; }
}

/// <summary>设计 §5 拒绝码与成功/幂等码（稳定 wire 值，Runtime 工具层直接透传给调用方）。</summary>
public static class TaskGoalLaunchCodes
{
    /// <summary>§5-1：workspace 不匹配或任务不存在。</summary>
    public const string TaskNotFound = "task.not_found";

    /// <summary>§5-2：ExpectedVersion CAS 不符。</summary>
    public const string VersionConflict = "task.version_conflict";

    /// <summary>§5-3：状态 ∉ {Backlog, Ready}（Completed/Cancelled/Archived/NeedsReview/InProgress 等均拒绝）；
    /// 亦用于「开关开启后评估集合中找不到该卡决策」的 fail-closed 兜底（评估器只评 Ready/Deferred）。</summary>
    public const string TaskNotDispatchable = "task_not_dispatchable";

    /// <summary>§5-4：任务被其他 Agent 持有（ActiveAssignmentId 非空且不属于自己，
    /// 含 attempt 缺失/已释放等无法确认归属的 fail-closed 形态）。</summary>
    public const string HeldByOtherAgent = "task_held_by_other_agent";

    /// <summary>§5-5：PreferredAgentId 指向他人（路由尊重）。</summary>
    public const string PreferredAgentMismatch = "task_preferred_agent_mismatch";

    /// <summary>§5-6：依赖不满足。实现说明：DependenciesStillSatisfiedAsync 为 TransactionStore
    /// 私有方法（TaskGoalDispatchTransactionStore.cs:530），服务层不复制围栏逻辑，依赖校验由
    /// Evaluator 前置（TaskAutoDispatchEvaluator.cs:291-306：非 Satisfied ⇒ 非 Eligible，
    /// 透传 task_dependency_waiting / task_dependency_broken）+ Store 运行时兜底
    /// （TaskGoalDispatchTransactionStore.cs:106 DependencyChanged）。本常量为设计码表占位，
    /// 当前主流程不直接产出。</summary>
    public const string DependenciesUnmet = "dependencies_unmet";

    /// <summary>§5-7a：调度器总开关关闭（TaskAutoDispatch:Enabled = false）。</summary>
    public const string SchedulerDisabled = "scheduler_disabled";

    /// <summary>§5-7b：调度器模式非 authoritative 家族（shadow/disabled 只评估不派发）。
    /// 该闸门位于调用方（TaskAutoDispatchScanRunner.RunAsync / TaskAutoDispatchWorker）而非
    /// Starter 内部，不自检即等于绕过调度器全局开关（设计 §5 关键风险点）。</summary>
    public const string SchedulerNotAuthoritative = "scheduler_not_authoritative";

    /// <summary>§5-7c：workspace 被运营暂停（TaskAutoDispatch:PausedWorkspaceIds，
    /// 消费端同款比较：StringComparer.Ordinal，TaskAutoDispatchWorker.cs:68）。</summary>
    public const string SchedulerPaused = "scheduler_paused";

    /// <summary>§5-8：该 task 已有活跃任务绑定 Goal ⇒ 幂等成功（Started=true，返回既有 GoalRunId），
    /// 不重复创建。</summary>
    public const string AlreadyRunning = "already_running";

    /// <summary>成功：单卡派发完成（对应 TaskAutoDispatchStartOutcome.Started = true）。</summary>
    public const string Started = "started";

    /// <summary>§5-10 兜底：派发结果缺失（理论上 Eligible 决策必有逐卡 outcome），
    /// fail-closed 拒绝。</summary>
    public const string StartRefused = "start_refused";
}
