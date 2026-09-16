using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// 「Agent 自主进入 Goal 模式」(<c>task_goal_start</c>) 的平台层实现
/// （设计 <c>Docs/Features/Agent自主进入Goal模式工具设计-2026-09-16.md</c> §5/§6）。
/// <para>
/// 只复用 canonical 调度链：开关走 <see cref="IWorkspaceTaskAdminService.UpdateTaskAsync"/> →
/// <c>TaskCommandService.PatchAsync</c>，评估走 <see cref="ITaskAutoDispatchEvaluator"/>，
/// 派发走 <see cref="ITaskAutoDispatchStarter.DispatchDetailedAsync"/>（maxStartsOverride: 1，
/// 只派当前这一张卡）；不复制围栏逻辑、不直改数据库、不新增第二条启动路径（设计 §2）。
/// </para>
/// <para>
/// §5-7 调度器闸门必须在此显式自检：<c>Enabled</c>/<c>Mode</c>/<c>PausedWorkspaceIds</c> 的判定
/// 位于调用方（TaskAutoDispatchScanRunner.RunAsync 的 mode 归一化与 Worker 的 paused 过滤
/// TaskAutoDispatchWorker.cs:68）而<strong>不在 Starter 内部</strong>——只调 Starter 等于绕过
/// 调度器全局开关。
/// </para>
/// </summary>
public sealed class TaskGoalLaunchService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IWorkspaceTaskAdminService taskAdmin,
    ITaskAutoDispatchEvaluator evaluator,
    ITaskAutoDispatchStarter starter,
    IOptionsMonitor<TaskAutoDispatchOptions> dispatchOptions,
    IOptions<TaskBoundGoalOptions> taskBoundOptions,
    ILogger<TaskGoalLaunchService> logger) : ITaskGoalLaunchService
{
    // 设计 §5-3 白名单：Backlog/Ready 可发起；Deferred 及其余九态一律拒绝。
    // （评估器候选只含 Ready/Deferred，TaskAutoDispatchEvaluator.cs:196-197/237-238；
    //   Backlog 卡会在评估步骤 fail-closed 兜底。）
    private static readonly IReadOnlySet<WorkspaceTaskStatus> LaunchableStatuses =
        new HashSet<WorkspaceTaskStatus> { WorkspaceTaskStatus.Backlog, WorkspaceTaskStatus.Ready };

    /// <inheritdoc />
    public async Task<TaskGoalLaunchResult> LaunchAsync(TaskGoalLaunchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        if (request.IterationBudget is < 1)
        {
            throw new ArgumentException(
                "IterationBudget must be at least 1 when provided.", nameof(request));
        }

        // ── §6-1：前置校验（§5 第 1–7 条，任一失败即返回，不产生任何写入）─────────
        // 单一只读上下文完成 任务/assignment/binding 三查，保证同一快照判定。
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // §5-1：workspace 匹配、任务存在。
            var task = await db.WorkspaceTasks.AsNoTracking().SingleOrDefaultAsync(
                item => item.WorkspaceId == request.WorkspaceId && item.TaskId == request.TaskId, ct);
            if (task is null)
            {
                return Refuse(TaskGoalLaunchCodes.TaskNotFound,
                    $"任务不存在（workspace={request.WorkspaceId}, task={request.TaskId}）。", null);
            }

            // §5-2：CAS。
            if (request.ExpectedVersion is { } expected && expected != task.Version)
            {
                return Refuse(TaskGoalLaunchCodes.VersionConflict,
                    $"任务版本冲突：期望 {expected}，当前 {task.Version}。", task.Version);
            }

            // §5-3：状态白名单。
            if (!LaunchableStatuses.Contains(task.Status))
            {
                return Refuse(TaskGoalLaunchCodes.TaskNotDispatchable,
                    $"任务当前状态 {task.Status} 不可发起 Goal（仅 Backlog/Ready 可发起）。", task.Version);
            }

            // §5-4：授权——ActiveAssignmentId 为空，或该 assignment 属于自己。
            // attempt 缺失/已释放 ⇒ 无法确认归属，fail-closed 拒绝（同码）。
            if (task.ActiveAssignmentId is not null)
            {
                var attempt = await db.TaskAssignmentAttempts.AsNoTracking().SingleOrDefaultAsync(
                    item => item.WorkspaceId == request.WorkspaceId
                        && item.AttemptId == task.ActiveAssignmentId, ct);
                if (attempt is null
                    || attempt.ReleasedAtUtc is not null
                    || !string.Equals(attempt.AgentId, request.AgentId, StringComparison.Ordinal))
                {
                    return Refuse(TaskGoalLaunchCodes.HeldByOtherAgent,
                        "任务正被其他 Agent 持有（存在不属于当前 Agent 的活跃 assignment）。", task.Version);
                }
            }

            // §5-5：路由尊重——PreferredAgentId 为空或等于自己。
            // 注意比 Store 围栏（AllowAgentFallback 可放宽，TaskGoalDispatchTransactionStore.cs:92-94）
            // 更严格：工具面不做 fallback 放行，按设计 §5-5 fail-closed。
            if (!string.IsNullOrWhiteSpace(task.PreferredAgentId)
                && !string.Equals(task.PreferredAgentId, request.AgentId, StringComparison.Ordinal))
            {
                return Refuse(TaskGoalLaunchCodes.PreferredAgentMismatch,
                    $"任务路由偏好指向其他 Agent（preferred_agent_id={task.PreferredAgentId}）。", task.Version);
            }

            // §5-7：调度器闸门（关键风险点，见类注释）。
            var options = dispatchOptions.CurrentValue;
            if (!options.Enabled)
            {
                return Refuse(TaskGoalLaunchCodes.SchedulerDisabled,
                    "调度器总开关关闭（TaskAutoDispatch:Enabled=false），无法发起 Goal。", task.Version);
            }

            if (!TaskAutoDispatchOptions.IsAuthoritativeMode(options.Mode))
            {
                return Refuse(TaskGoalLaunchCodes.SchedulerNotAuthoritative,
                    $"调度器当前模式为 {options.Mode}（非 authoritative 家族，只评估不派发），无法发起 Goal。",
                    task.Version);
            }

            if (options.PausedWorkspaceIds.Contains(request.WorkspaceId, StringComparer.Ordinal))
            {
                return Refuse(TaskGoalLaunchCodes.SchedulerPaused,
                    "工作区被调度器暂停（TaskAutoDispatch:PausedWorkspaceIds），无法发起 Goal。", task.Version);
            }

            // ── §6-2：幂等（§5-8）。命中 ⇒ 返回既有 GoalRunId，不重复创建、不写开关。
            // 判定查询为本服务新增的最小只读查询（既有 activeBinding 判定
            // TaskGoalDispatchTransactionStore.cs:139-141 额外按 AgentId 过滤、服务于 AgentNotIdle
            // 语义，不可复用）；task_goal_bindings 对 (workspace, task) 维持 1:1 活跃绑定
            // （TaskGoalBindingEntity 类注释），Single 足够。
            var activeBinding = await db.TaskGoalBindings.AsNoTracking().SingleOrDefaultAsync(
                item => item.WorkspaceId == request.WorkspaceId
                    && item.TaskId == request.TaskId
                    && item.Status == "active", ct);
            if (activeBinding is not null)
            {
                logger.LogInformation(
                    "[TaskGoalLaunch] idempotent hit task={TaskId} goalRun={GoalRunId}",
                    request.TaskId,
                    activeBinding.GoalRunId);
                return new TaskGoalLaunchResult
                {
                    Started = true,
                    Code = TaskGoalLaunchCodes.AlreadyRunning,
                    GoalRunId = activeBinding.GoalRunId,
                    AssignmentId = activeBinding.AssignmentId,
                    TaskVersion = task.Version,
                    Message = $"该任务已有活跃的任务绑定 Goal（goal_run={activeBinding.GoalRunId}），幂等返回既有运行。",
                };
            }

            // ── §6-3：确保 auto_dispatch_enabled = true（canonical 任务级 opt-in）。
            // 不传 ExpectedVersion：WorkspaceTaskAdminService.cs:173 缺省时自读当前版本做 CAS。
            if (!task.AutoDispatchEnabled)
            {
                await taskAdmin.UpdateTaskAsync(new TaskAdminUpdateRequest
                {
                    WorkspaceId = request.WorkspaceId,
                    TaskId = request.TaskId,
                    AutoDispatchEnabled = true,
                    ActorId = request.AgentId,
                }, ct);
            }
        }

        // ── §6-4：评估（evaluate-only，无副作用），取目标卡决策。
        var snapshot = dispatchOptions.CurrentValue;
        var limit = Math.Clamp(snapshot.CandidateLimit, 1, 500);
        var decisions = await evaluator.EvaluateAsync(request.WorkspaceId, limit, ct);
        var decision = decisions.FirstOrDefault(item =>
            string.Equals(item.TaskId, request.TaskId, StringComparison.Ordinal));
        if (decision is null)
        {
            // Backlog 或 next_eligible 未到等形态：评估集合不含该卡 ⇒ fail-closed。
            return Refuse(TaskGoalLaunchCodes.TaskNotDispatchable,
                "任务不在当前可评估集合中（评估器只评估 Ready/Deferred 且已开启调度的候选）。", null);
        }

        // §5-9：非 Eligible ⇒ 透传决策码（agent_busy / task_dependency_waiting / window_refused 等）。
        // 依赖满足（§5-6）由此隐含保证：Evaluator.cs:291-306 对非 Satisfied 依赖不产出 Eligible 决策。
        if (decision.Verdict != TaskAutoDispatchCandidateVerdict.Eligible)
        {
            logger.LogInformation(
                "[TaskGoalLaunch] evaluation refused task={TaskId} verdict={Verdict} code={Code}",
                request.TaskId,
                decision.Verdict,
                decision.Code);
            return Refuse(decision.Code,
                $"任务未通过调度评估（verdict={decision.Verdict}）：{decision.Code}。",
                decision.TaskVersion);
        }

        // ── 预算 clamp（设计 §2-6）：min(请求, 配置)，不得抬高。
        var configuredBudget = taskBoundOptions.Value.GoalIterationBudget;
        var effectiveBudget = request.IterationBudget is { } requested
            ? Math.Min(requested, configuredBudget)
            : configuredBudget;
        if (request.IterationBudget is { } wanted && wanted < configuredBudget)
        {
            // 管道现状：GoalIterationBudget 由 Starter 从 IOptions<TaskBoundGoalOptions> 硬编码
            // （TaskAutoDispatchStarter.DispatchDetailedAsync → StartGoalFromTaskCommand），无外部
            // 注入点 ⇒ 请求的更小预算无法经管道生效。「不得抬高」由本 clamp + Starter 硬编码共同
            // 保证；此处如实告知调用方实际生效值。
            logger.LogWarning(
                "[TaskGoalLaunch] requested budget {Requested} below configured {Configured}; pipeline applies configured value (no external budget injection point)",
                wanted,
                configuredBudget);
        }

        // ── §6-5：派发——只派这一张卡，绝不全量扫描。
        var outcomes = await starter.DispatchDetailedAsync(
            new[] { decision }, maxStartsOverride: 1, ct);
        var outcome = outcomes.FirstOrDefault(item =>
            string.Equals(item.TaskId, request.TaskId, StringComparison.Ordinal));
        if (outcome is null)
        {
            logger.LogError(
                "[TaskGoalLaunch] starter returned no outcome for eligible task={TaskId}",
                request.TaskId);
            return Refuse(TaskGoalLaunchCodes.StartRefused,
                "派发未返回该卡结果（fail-closed 拒绝）。", decision.TaskVersion);
        }

        if (!outcome.Started)
        {
            // §5-10：透传派发拒绝码（incomplete_candidate / window_refused / task_goal lost race 等）。
            logger.LogInformation(
                "[TaskGoalLaunch] start refused task={TaskId} code={Code}",
                request.TaskId,
                outcome.Code);
            return Refuse(outcome.Code,
                $"任务绑定 Goal 启动被拒：{outcome.Code}。", decision.TaskVersion);
        }

        logger.LogInformation(
            "[TaskGoalLaunch] started task={TaskId} goalRun={GoalRunId} assignment={AssignmentId} budget={Budget}",
            request.TaskId,
            outcome.GoalRunId,
            outcome.AssignmentId,
            effectiveBudget);
        return new TaskGoalLaunchResult
        {
            Started = true,
            Code = TaskGoalLaunchCodes.Started,
            GoalRunId = outcome.GoalRunId,
            AssignmentId = outcome.AssignmentId,
            TaskVersion = decision.TaskVersion,
            Message = string.IsNullOrWhiteSpace(request.Reason)
                ? $"任务绑定 Goal 已启动（goal_run={outcome.GoalRunId}），迭代预算 {effectiveBudget}。"
                : $"任务绑定 Goal 已启动（goal_run={outcome.GoalRunId}），迭代预算 {effectiveBudget}。原因：{request.Reason}",
        };
    }

    private static TaskGoalLaunchResult Refuse(string code, string message, int? taskVersion) => new()
    {
        Started = false,
        Code = code,
        Message = message,
        TaskVersion = taskVersion,
    };
}
