using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>ADR-074 §10: Goal 只读投影。任意入口看到的状态都来自同一服务端查询。</summary>
public sealed class GoalQueryService(
    GoalRunStore store,
    PlatformDbContext db,
    GoalCheckRecordStore checkRecords,
    ITodoStore todoStore) : IGoalQueryService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>步骤终态口径，与 GoalRunStore.FindCurrentTaskWorkUnitAsync 完全一致，不得发散。</summary>
    private static readonly string[] TerminalStepStatuses =
    [
        nameof(TaskNodeStatuses.Completed),
        nameof(TaskNodeStatuses.Cancelled),
        nameof(TaskNodeStatuses.Superseded),
    ];

    public async Task<GoalSnapshot?> GetActiveAsync(
        string workspaceId, string conversationId, CancellationToken ct = default)
    {
        var latest = await store.FindLatestAsync(workspaceId, conversationId, ct);
        return latest is not null && !GoalStateMachine.IsTerminal(latest.Status)
            ? latest.ToSnapshot()
            : null;
    }

    public async Task<GoalSnapshot?> GetAsync(string goalRunId, CancellationToken ct = default)
    {
        var goal = await store.FindAsync(goalRunId, ct);
        return goal?.ToSnapshot();
    }

    public async Task<GoalSnapshot?> GetLatestAsync(
        string workspaceId, string conversationId, CancellationToken ct = default)
    {
        var latest = await store.FindLatestAsync(workspaceId, conversationId, ct);
        // ADR-074 §11：clear 只清除已结束 Goal 的展示，不删除事件/Iteration/Verification。
        // 已清除的记录不得再作为"当前 Goal"回吐，否则 Banner 会永久挂着一枚用户已明确
        // 关闭的历史终态徽标（clear 写入的 ClearedAtUtc 必须被读路径尊重，否则该命令是空操作）。
        return latest is null || latest.ClearedAtUtc is not null
            ? null
            : latest.ToSnapshot();
    }

    /// <summary>
    /// TD-2：目标 → 归属 Agent 拆解 TODO（todo_lists/todo_items）只读投影。
    /// agent_id 隔离硬约束（TodoContracts TD-1b）：todo_lists 无 workspace 维度，
    /// 归属 Agent 必须服务端从 goal 行解析，绝不接受客户端传入，否则可跨工作区越权读。
    /// </summary>
    public async Task<GoalTodoSnapshot?> GetTodoAsync(string goalRunId, CancellationToken ct = default)
    {
        var goal = await store.FindAsync(goalRunId, ct);
        if (goal is null)
            return null;

        var list = await todoStore.ReadAsync(
            new TodoReadQuery
            {
                AgentId = goal.AgentInstanceId,
                ScopeKind = TodoWireMaps.ScopeGoal,
                ScopeId = goal.GoalRunId,
            },
            ct);

        // 列表不存在 ⇒ Found=false（与 todo_read 工具同语义，不是错误）；仅 goal 不存在才由控制器映射 404。
        return new GoalTodoSnapshot
        {
            GoalRunId = goal.GoalRunId,
            AgentInstanceId = goal.AgentInstanceId,
            Found = list is not null,
            ListId = list?.ListId,
            Title = list?.Title,
            Revision = list?.Revision ?? 0,
            Items = list?.Items ?? [],
            Summary = list?.Summary,
        };
    }

    public async Task<IReadOnlyList<GoalIterationSnapshot>> GetIterationsAsync(
        string goalRunId, CancellationToken ct = default)
    {
        var iterations = await store.GetIterationsAsync(goalRunId, ct);
        return iterations
            .Select(i => new GoalIterationSnapshot
            {
                GoalRunId = i.GoalRunId,
                ActivationEpoch = i.ActivationEpoch,
                IterationNo = i.IterationNo,
                Status = i.Status,
                CommandId = i.CommandId,
                TurnId = i.TurnId,
                StartedAtUtc = i.StartedAtUtc,
                SettledAtUtc = i.SettledAtUtc,
            })
            .ToList();
    }

    public async Task<GoalStepsSnapshot?> GetStepsAsync(string goalRunId, CancellationToken ct = default)
    {
        var goal = await store.FindAsync(goalRunId, ct);
        if (goal is null)
            return null;

        var binding = await store.FindTaskBindingAsync(goalRunId, ct);
        var planId = binding?.TaskPlanId;

        // 只读探索冻结计划：plan run 行 + depth1 叶子。任一缺失都按"无冻结计划"投影（不抛 404）。
        TaskPlanRunEntity? plan = null;
        var leaves = new List<TaskNodeEntity>();
        if (!string.IsNullOrWhiteSpace(planId))
        {
            plan = await db.TaskPlanRuns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.PlanId == planId, ct);

            if (plan is not null)
            {
                leaves = await db.TaskNodes.AsNoTracking()
                    .Where(item => item.PlanId == planId && item.Depth == 1)
                    .OrderBy(item => item.SequenceNo)
                    // 注意：不能带 IComparer 重载（EF 无法翻译，会抛 InvalidOperationException）。
                    .ThenBy(item => item.TaskNodeId)
                    .ToListAsync(ct);
            }
        }

        var hasPlan = plan is not null && leaves.Count > 0;

        // currentStepId 必须复用 GoalRunStore.FindCurrentTaskWorkUnitAsync（首个非终态 depth1 叶子，
        // 按 sequence_no）：这是 W2 硬约束，另写一套判断会与结算侧的当前步骤选定发散。
        var currentStepId = hasPlan
            ? (await store.FindCurrentTaskWorkUnitAsync(plan!.PlanId, ct))?.TaskNodeId
            : null;

        // work_unit_await_handles 有 (plan_id, task_node_id) 外键，是步骤上真实存在的关联引用；
        // 注意：SQLite provider 不翻译 DateTimeOffset ORDER BY（见 GoalCheckRecordStore 同类注释），
        // 排序必须在内存完成，否则整条查询在编译期抛 InvalidOperationException → 接口 500。
        var awaitHandlesByNode = plan is not null
            ? (await db.WorkUnitAwaitHandles.AsNoTracking()
                    .Where(item => item.PlanId == plan.PlanId)
                    .ToListAsync(ct))
                .OrderBy(item => item.CreatedAtUtc)
                .GroupBy(item => item.TaskNodeId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal)
            : new Dictionary<string, List<WorkUnitAwaitHandleEntity>>(StringComparer.Ordinal);

        var steps = leaves.ConvertAll(item => new GoalStepSnapshot
        {
            NodeId = item.TaskNodeId,
            SequenceNo = item.SequenceNo,
            Kind = item.WorkUnitKind,
            Title = item.Title,
            Status = item.Status,
            StartedAtUtc = FromUnixMillisecondsNullable(item.StartedAt),
            CompletedAtUtc = FromUnixMillisecondsNullable(item.CompletedAt),
            // task_nodes 无 blocker code 列：不伪造，如实留 null。
            BlockerCode = null,
            EvidenceRefs = BuildStepEvidenceRefs(item, awaitHandlesByNode),
        });

        var progress = new GoalStepProgressSnapshot
        {
            StepsTotal = leaves.Count,
            StepsPassed = leaves.Count(item => IsStatus(item.Status, TaskNodeStatuses.Completed)),
            StepsFailed = leaves.Count(item => IsStatus(item.Status, TaskNodeStatuses.Failed)),
            // 与 FindCurrentTaskWorkUnitAsync 的终态口径一致：Completed/Cancelled/Superseded 之外都计为进行中。
            StepsInProgress = leaves.Count(item => !IsTerminalStepStatus(item.Status)),
            CurrentStepId = currentStepId,
        };

        // checks 是目标级投影：goal_check_records 只有 goal_run_id 外键，与具体步骤没有关联。
        // 仅取当前 activation epoch —— 旧 epoch 的报告是旧进程的证据（ADR-092 §6.2），不得回吐。
        var checkRecordsList = await checkRecords.ReadForEpochAsync(goalRunId, goal.ActivationEpoch, ct);
        var checks = checkRecordsList.Select(ToCheckSnapshot).ToList();

        return new GoalStepsSnapshot
        {
            GoalRunId = goal.GoalRunId,
            Phase = goal.Status,
            HasPlan = hasPlan,
            PlanVersion = hasPlan ? plan!.PlanVersion : null,
            PlanRevision = hasPlan ? plan!.PlanRevision : null,
            Progress = progress,
            Steps = steps,
            Checks = checks,
        };
    }

    private static GoalCheckSnapshot ToCheckSnapshot(GoalCheckRecordEntity record)
    {
        // 与 GoalVerificationPersistence.ReadReports 同一口径：只有 finished 且带 ReportJson 才有可用
        // 报告；JSON 非法按"无报告"处理（fail-closed），status 回落到记录生命周期状态，
        // 绝不把缺失伪造成 passed。
        GoalCheckReport? report = null;
        if (string.Equals(record.Status, GoalCheckRecordStatuses.Finished, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(record.ReportJson))
        {
            try
            {
                report = JsonSerializer.Deserialize<GoalCheckReport>(record.ReportJson, JsonOpts);
            }
            catch (JsonException)
            {
                report = null;
            }
        }

        return new GoalCheckSnapshot
        {
            CheckId = record.CheckId,
            CriterionId = record.CriterionId,
            Status = report?.Status ?? record.Status,
            ExitCode = report?.ExitCode,
            Summary = report?.Message,
            EvidenceRefs = report?.EvidenceRefs ?? [],
        };
    }

    private static IReadOnlyList<string> BuildStepEvidenceRefs(
        TaskNodeEntity node,
        IReadOnlyDictionary<string, List<WorkUnitAwaitHandleEntity>> awaitHandlesByNode)
    {
        // 只投射真实存在的关联：节点自身的产物/检查点引用 + await handle 外键。
        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(node.ResultArtifactRef))
            refs.Add(node.ResultArtifactRef);
        if (!string.IsNullOrWhiteSpace(node.CheckpointArtifactRef))
            refs.Add(node.CheckpointArtifactRef);
        if (awaitHandlesByNode.TryGetValue(node.TaskNodeId, out var handles))
            refs.AddRange(handles.Select(handle => $"await-handle:{handle.AwaitHandleId}"));
        return refs;
    }

    private static bool IsStatus(string status, TaskNodeStatuses expected)
        => string.Equals(status, expected.ToString(), StringComparison.Ordinal);

    private static bool IsTerminalStepStatus(string status)
        => TerminalStepStatuses.Contains(status, StringComparer.Ordinal);

    private static DateTimeOffset? FromUnixMillisecondsNullable(long? value)
        => value is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
