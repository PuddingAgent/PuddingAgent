using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Services.Goals;

public sealed record GoalSettlementCandidate
{
    public required string GoalIterationId { get; init; }
    public required string GoalRunId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int AggregateVersion { get; init; }
    public required int IterationNo { get; init; }
    public required int MaxIterations { get; init; }
    public required int IterationsStarted { get; init; }
    public required string Objective { get; init; }
    public required int ObjectiveVersion { get; init; }
    public required string TurnId { get; init; }
    public required string TerminalKind { get; init; }
    public required long TerminalSequence { get; init; }
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
    /// <summary>Error code from the terminal turn.failed payload (e.g. work_unit_budget_exhausted).</summary>
    public string? ErrorCode { get; init; }
    /// <summary>Error message from the terminal turn.failed payload (e.g. WorkUnit input Token budget exhausted).</summary>
    public string? ErrorMessage { get; init; }
    public string? TaskId { get; init; }
    public int? TaskVersion { get; init; }
    public string? TaskStatus { get; init; }
    public string? TaskAcceptanceCriteria { get; init; }
    /// <summary>绑定执行计划的指纹（合同 matching 用；未绑定计划时为 null）。</summary>
    public string? PlanFingerprint { get; init; }
    /// <summary>
    /// ADR-092 §6.2（G92-1 P1）：本次裁决的作用域，取值见 <see cref="GoalVerificationScopes"/>。
    /// 由绑定执行计划推导（最后一个 WorkUnit = goal）；计划不可读时保守为 work_unit。
    /// </summary>
    public string VerificationScope { get; init; } = GoalVerificationScopes.WorkUnit;
    /// <summary>
    /// ADR-092 §6.2（G92-1 P1）：绑定计划中尚未完成的 WorkUnit 数；null 表示未知。
    /// fail-closed：未知一律不得据此宣布整体完成；为 0 时完成路径才可达。
    /// </summary>
    public int? RemainingWorkUnits { get; init; }
    /// <summary>持久验收合同的必需条件（空 = 尚未派生合同，必须走有界修复而非 vacuous pass）。</summary>
    public IReadOnlyList<GoalCriterion> Criteria { get; init; } = [];
    /// <summary>持久验收合同的版本化检查定义。</summary>
    public IReadOnlyList<GoalCheckSpec> Checks { get; init; } = [];
    /// <summary>持久检查记录中可信的 finished 报告（pending/leased/无报告一律不入）。</summary>
    public IReadOnlyList<GoalCheckReport> CheckReports { get; init; } = [];

    /// <summary>G92-1 S1-a：验收合同来源（goal_acceptance_contracts.source）；合同行缺失时为 null。</summary>
    public string? AcceptanceContractSource { get; init; }

    public bool HasPendingExecutionFacts { get; init; }
    public bool EvidenceComplete { get; init; }
    public string? RunId { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public long ActiveElapsedMs { get; init; }
    public int LlmRounds { get; init; }
    public int ToolCalls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }

    public GoalEvidenceCapsule ToCapsule() => new()
    {
        GoalRunId = GoalRunId,
        ActivationEpoch = ActivationEpoch,
        AggregateVersion = AggregateVersion,
        IterationNo = IterationNo,
        Objective = Objective,
        ObjectiveVersion = ObjectiveVersion,
        RemainingIterations = Math.Max(0, MaxIterations - IterationsStarted),
        TurnId = TurnId,
        TerminalKind = TerminalKind,
        TerminalSequence = TerminalSequence,
        EvidenceRefs = EvidenceRefs,
        TaskId = TaskId,
        TaskStatus = TaskStatus,
        TaskAcceptanceCriteria = TaskAcceptanceCriteria,
        // G92-1 P1：作用域与剩余 WorkUnit 必须随 capsule 进入 verifier，否则 Task-bound Goal 的完成永远停在 work_unit。
        VerificationScope = VerificationScope,
        RemainingWorkUnits = RemainingWorkUnits,
        HasPendingExecutionFacts = HasPendingExecutionFacts,
        EvidenceComplete = EvidenceComplete,
        Criteria = Criteria,
        Checks = Checks,
        CheckReports = CheckReports,
        // G92-1 S1-a：合同来源随胶囊进入 verifier，覆盖门据它拒绝无目标级覆盖的完成。
        AcceptanceContractSource = AcceptanceContractSource,
    };
}

/// <summary>
/// Canonical Turn 终态到 Goal Iteration/Verification/下一 continuation 的唯一事务协调器。
/// Verifier 只返回建议；本 Store 在事务内重验 epoch/version/Task 状态并独占终态写入。
/// </summary>
public sealed class GoalSettlementStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ICommittedEventSignal committedSignal,
    GoalOutboxSignal outboxSignal,
    IOptions<TaskBoundGoalOptions>? taskBoundOptions = null,
    IOptions<GoalRunOptions>? goalOptions = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _reservationLease =
        taskBoundOptions?.Value.ReservationLease ?? TimeSpan.FromHours(2);

    // P0-2（ADR-092 §7）：无进展/同阻塞熔断阈值；未配置或配置非法时 fail-safe 回落默认 3。
    private readonly int _noProgressBreakerThreshold =
        goalOptions?.Value.NoProgressBreakerThreshold is int breakerThreshold and > 0
            ? Math.Clamp(breakerThreshold, 1, 16)
            : 3;

    public async Task<IReadOnlyList<GoalSettlementCandidate>> GetCandidatesAsync(
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var openCandidates = await db.GoalIterations.AsNoTracking()
            .Where(item => item.Status == "accepted" || item.Status == "running")
            .ToListAsync(ct);
        // SQLite 不支持 DateTimeOffset ORDER BY；活动 Iteration 受单 Goal 单飞约束有界。
        var openIterations = openCandidates
            .OrderBy(item => item.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 64))
            .ToList();
        var results = new List<GoalSettlementCandidate>();

        foreach (var iteration in openIterations)
        {
            if (string.IsNullOrWhiteSpace(iteration.TurnId))
                continue;
            var turn = await db.ConversationTurns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TurnId == iteration.TurnId, ct);
            if (turn?.TerminalSequence is null
                || turn.Status is not ("completed" or "failed" or "cancelled"))
                continue;
            var goal = await db.GoalRuns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.GoalRunId == iteration.GoalRunId, ct);
            if (goal is null)
                continue;

            var binding = await db.TaskGoalBindings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.GoalRunId == goal.GoalRunId, ct);
            WorkspaceTaskEntity? task = null;
            if (binding is not null)
            {
                task = await db.WorkspaceTasks.AsNoTracking().SingleOrDefaultAsync(
                    item => item.WorkspaceId == binding.WorkspaceId
                        && item.TaskId == binding.TaskId,
                    ct);
            }

            // ADR-092 §5.1/§6.2：验收条件与检查报告只能来自持久记录。
            // 没有合同行就是"尚未派生合同"（空合同），没有 finished 报告就是"未运行"，
            // 两者都绝不在生产代码里用假 passed 代替。
            var contract = await db.GoalAcceptanceContracts.AsNoTracking().SingleOrDefaultAsync(
                item => item.GoalRunId == goal.GoalRunId
                    && item.ActivationEpoch == iteration.ActivationEpoch
                    && item.ObjectiveVersion == goal.ObjectiveVersion,
                ct);
            var checkRecords = await db.GoalCheckRecords.AsNoTracking()
                .Where(item => item.GoalRunId == goal.GoalRunId
                    && item.ActivationEpoch == iteration.ActivationEpoch)
                .ToListAsync(ct);

            var expectedTerminalType = turn.TerminalKind switch
            {
                "completed" => ConversationEventTypes.TurnCompleted,
                "failed" => ConversationEventTypes.TurnFailed,
                "cancelled" => ConversationEventTypes.TurnCancelled,
                _ => string.Empty,
            };
            var evidenceQuery = db.ConversationEvents.AsNoTracking()
                .Where(item => item.ConversationId == goal.CurrentConversationId
                    && item.TurnId == iteration.TurnId
                    && item.Sequence >= (iteration.AcceptedSequence ?? 0)
                    && item.Sequence <= turn.TerminalSequence.Value);
            var evidenceComplete = !string.IsNullOrWhiteSpace(expectedTerminalType)
                && await evidenceQuery.AnyAsync(item => item.Type == expectedTerminalType, ct);
            // Keep the evidence capsule bounded, but retain the terminal edge. Taking
            // the oldest 128 events drops turn.completed when a model streams many
            // thinking deltas and incorrectly blocks an otherwise terminal iteration.
            var evidenceEvents = await evidenceQuery
                .OrderByDescending(item => item.Sequence)
                .Take(128)
                .Select(item => new { item.EventId, item.Type, item.Sequence })
                .ToListAsync(ct);
            evidenceEvents.Reverse();
            var hasPending = await db.ExecutionRuns.AsNoTracking().AnyAsync(
                item => item.TurnId == iteration.TurnId
                    && (item.Status == "leased"
                        || item.Status == "running"
                        || item.Status == "cancel_requested"),
                ct);
            var refs = evidenceEvents
                .Select(item => $"conversation-event:{item.EventId}")
                .ToList();
            if (task is not null)
                refs.Add($"workspace-task:{task.TaskId}:version:{task.Version}:status:{task.Status}");
            var usagePayloads = await evidenceQuery
                .Where(item => item.Type == ConversationEventTypes.UsageRecorded)
                .OrderBy(item => item.Sequence)
                .Select(item => item.Payload)
                .ToListAsync(ct);
            var (llmRounds, inputTokens, outputTokens) = SumUsage(usagePayloads);
            var delegatedUsage = await SumDelegatedUsageAsync(
                db,
                goal.CurrentConversationId,
                DateTimeOffset.FromUnixTimeMilliseconds(turn.CreatedAt),
                turn.CompletedAt is long completedAt
                    ? DateTimeOffset.FromUnixTimeMilliseconds(completedAt)
                    : null,
                ct);
            llmRounds += delegatedUsage.Rounds;
            inputTokens += delegatedUsage.InputTokens;
            outputTokens += delegatedUsage.OutputTokens;
            var toolCalls = await evidenceQuery.CountAsync(
                item => item.Type == ConversationEventTypes.ToolCallRequested,
                ct);
            var execution = await db.ExecutionRuns.AsNoTracking()
                .Where(item => item.TurnId == iteration.TurnId)
                .OrderByDescending(item => item.Attempt)
                .Select(item => new { item.RunId, item.StartedAt, item.CompletedAt })
                .FirstOrDefaultAsync(ct);
            var startedAtUtc = execution?.StartedAt is long startedAt
                ? DateTimeOffset.FromUnixTimeMilliseconds(startedAt)
                : (DateTimeOffset?)null;
            var activeElapsedMs = execution is { StartedAt: long started, CompletedAt: long completed }
                ? Math.Max(0, completed - started)
                : 0;
            string? failureCode = null;
            string? failureMessage = null;
            if (string.Equals(turn.Status, "failed", StringComparison.Ordinal))
            {
                var terminalPayload = await evidenceQuery
                    .Where(item => item.Type == ConversationEventTypes.TurnFailed)
                    .OrderByDescending(item => item.Sequence)
                    .Select(item => item.Payload)
                    .FirstOrDefaultAsync(ct);
                (failureCode, failureMessage) = ExtractTurnFailure(terminalPayload);
            }

            // ADR-092 §6.2（G92-1 P1）：verifier 需要知道本次裁决的作用域与剩余 WorkUnit 数，
            // 否则 Task-bound Goal 的完成永远停在 work_unit。数据来源与 ApplyBoundPlanGates 完全同源
            // （同一 LoadBoundPlanAsync 判定）；计划缺失/不可读时 fail-closed 为 work_unit + null。
            var (verificationScope, remainingWorkUnits) = ResolveVerificationScope(
                await LoadBoundPlanAsync(db, binding, ct));

            results.Add(new GoalSettlementCandidate
            {
                GoalIterationId = iteration.GoalIterationId,
                GoalRunId = goal.GoalRunId,
                WorkspaceId = goal.WorkspaceId,
                ConversationId = goal.CurrentConversationId,
                AgentInstanceId = goal.AgentInstanceId,
                ActivationEpoch = iteration.ActivationEpoch,
                AggregateVersion = goal.AggregateVersion,
                IterationNo = iteration.IterationNo,
                MaxIterations = goal.MaxIterations,
                IterationsStarted = goal.IterationsStarted,
                Objective = goal.Objective,
                ObjectiveVersion = goal.ObjectiveVersion,
                TurnId = iteration.TurnId,
                TerminalKind = turn.TerminalKind!,
                TerminalSequence = turn.TerminalSequence.Value,
                EvidenceRefs = refs,
                ErrorCode = failureCode,
                ErrorMessage = failureMessage,
                TaskId = task?.TaskId,
                TaskVersion = task?.Version,
                TaskStatus = task?.Status.ToString(),
                TaskAcceptanceCriteria = task?.AcceptanceCriteria,
                PlanFingerprint = binding?.PlanFingerprint,
                VerificationScope = verificationScope,
                RemainingWorkUnits = remainingWorkUnits,
                Criteria = GoalVerificationPersistence.ReadCriteria(contract?.CriteriaJson),
                Checks = GoalVerificationPersistence.ReadChecks(contract?.ChecksJson),
                CheckReports = GoalVerificationPersistence.ReadReports(checkRecords),
                // G92-1 S1-a：合同行已随本候选一次性读取，来源随行携带，不新增第二次真值查询。
                AcceptanceContractSource = contract?.Source,
                HasPendingExecutionFacts = hasPending,
                EvidenceComplete = evidenceComplete,
                RunId = execution?.RunId,
                StartedAtUtc = startedAtUtc,
                ActiveElapsedMs = activeElapsedMs,
                LlmRounds = llmRounds,
                ToolCalls = toolCalls,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            });
        }

        return results;
    }

    private static (string? Code, string? Message) ExtractTurnFailure(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return (null, null);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);
            var code = ReadFailureString(root, "errorCode") ?? ReadFailureString(root, "code");
            var message = ReadFailureString(root, "errorMessage") ?? ReadFailureString(root, "message");
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? ReadFailureString(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static (int Rounds, long InputTokens, long OutputTokens) SumUsage(
        IReadOnlyList<string> payloads)
    {
        var rounds = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        foreach (var payload in payloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("usage", out var usageElement)
                    || usageElement.ValueKind != JsonValueKind.Object)
                    continue;
                var usage = JsonSerializer.Deserialize<TokenUsageDto>(usageElement.GetRawText(), JsonOpts);
                if (usage is null)
                    continue;
                rounds++;
                inputTokens += usage.PromptTokens ?? 0;
                outputTokens += usage.CompletionTokens ?? 0;
            }
            catch (JsonException)
            {
                // The canonical event remains evidence, but malformed accounting
                // metadata must not stop deterministic Goal settlement.
            }
        }
        return (rounds, inputTokens, outputTokens);
    }

    private static async Task<(int Rounds, long InputTokens, long OutputTokens)> SumDelegatedUsageAsync(
        PlatformDbContext db,
        string rootSessionId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        CancellationToken ct)
    {
        if (completedAtUtc is null || completedAtUtc < startedAtUtc)
            return default;

        // Each spawn receives a distinct sub-session id. Resolve the whole bounded
        // descendant tree instead of accounting only the first delegation level.
        // A visited set also makes malformed historical cycles harmless.
        const int maxAttributedSessions = 256;
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootSessionId };
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new[] { rootSessionId };
        while (frontier.Length > 0 && visited.Count < maxAttributedSessions)
        {
            var children = await db.SessionSubAgents.AsNoTracking()
                .Where(item => frontier.Contains(item.ParentSessionId))
                .Select(item => item.SubSessionId)
                .ToListAsync(ct);
            var remaining = maxAttributedSessions - visited.Count;
            frontier = children
                .Where(item => !string.IsNullOrWhiteSpace(item) && visited.Add(item))
                .Take(remaining)
                .ToArray();
            descendants.UnionWith(frontier);
        }

        if (descendants.Count == 0)
            return default;

        var descendantIds = descendants.ToArray();
        // SQLite's DateTimeOffset range translation is provider-sensitive. Session
        // ids are indexed and bounded, so load only descendant facts and apply the
        // exact Turn window in memory.
        var usageRows = await db.TokenUsageEvents.AsNoTracking()
            .Where(item => item.SessionId != null && descendantIds.Contains(item.SessionId))
            .Select(item => new
            {
                item.OccurredAtUtc,
                item.PromptTokens,
                item.CompletionTokens,
            })
            .ToListAsync(ct);
        var inWindow = usageRows
            .Where(item => item.OccurredAtUtc >= startedAtUtc
                && item.OccurredAtUtc <= completedAtUtc.Value)
            .ToList();
        return (
            inWindow.Count,
            inWindow.Sum(item => item.PromptTokens),
            inWindow.Sum(item => item.CompletionTokens));
    }

    public async Task<bool> ApplyAsync(
        GoalSettlementCandidate candidate,
        GoalVerificationDecision proposed,
        CancellationToken ct = default)
    {
        var nextContinuation = false;
        string? signalledConversation = null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var iteration = await db.GoalIterations.SingleOrDefaultAsync(
                item => item.GoalIterationId == candidate.GoalIterationId, ct);
            if (iteration is null || iteration.Status is not ("accepted" or "running"))
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            var turn = await db.ConversationTurns.AsNoTracking().SingleOrDefaultAsync(
                item => item.TurnId == iteration.TurnId, ct);
            if (turn?.TerminalSequence is null
                || turn.TerminalSequence != candidate.TerminalSequence
                || turn.Status is not ("completed" or "failed" or "cancelled"))
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            var goal = await db.GoalRuns.SingleOrDefaultAsync(
                item => item.GoalRunId == iteration.GoalRunId, ct);
            if (goal is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            var binding = await db.TaskGoalBindings.SingleOrDefaultAsync(
                item => item.GoalRunId == goal.GoalRunId, ct);
            WorkspaceTaskEntity? task = null;
            if (binding is not null)
            {
                task = await db.WorkspaceTasks.SingleOrDefaultAsync(
                    item => item.WorkspaceId == binding.WorkspaceId
                        && item.TaskId == binding.TaskId,
                    ct);
            }

            if (binding is not null
                && (binding.Status != "active"
                    || task is null
                    || task.TaskId != candidate.TaskId
                    || task.Version != candidate.TaskVersion))
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            var reservationValid = binding is null || await db.AgentExecutionReservations.AnyAsync(
                item => item.ReservationId == binding.ReservationId
                    && item.FencingToken == binding.ReservationFencingToken
                    && item.TaskId == binding.TaskId
                    && item.AgentId == binding.AgentInstanceId
                    && item.GoalRunId == binding.GoalRunId
                    && item.Status == "active", ct);

            var decision = ApplyDeterministicGates(proposed, task, candidate);
            var boundPlan = await LoadBoundPlanAsync(db, binding, ct);
            decision = ApplyBoundPlanGates(decision, candidate, boundPlan);
            if (!reservationValid)
            {
                decision = BlockedDecision(
                    "reservation_fence_lost",
                    "The Task-bound Goal reservation lease or fencing token is no longer authoritative.",
                    candidate.EvidenceRefs);
            }
            var now = DateTimeOffset.UtcNow;
            iteration.Status = turn.Status switch
            {
                "failed" => "failed",
                "cancelled" => "cancelled",
                _ => "settled",
            };
            iteration.TerminalSequence = turn.TerminalSequence;
            iteration.StopReason = turn.TerminalKind;
            // Archive the authoritative root cause: goal_iterations.error_id must
            // carry the real errorCode from the terminal turn.failed payload
            // (e.g. work_unit_budget_exhausted) instead of staying null.
            if (string.Equals(iteration.Status, "failed", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(candidate.ErrorCode))
            {
                iteration.ErrorId = candidate.ErrorCode;
            }
            iteration.SettledAtUtc = now;
            iteration.RunId ??= candidate.RunId;
            iteration.StartedAtUtc ??= candidate.StartedAtUtc;
            iteration.LlmRounds = candidate.LlmRounds;
            iteration.ToolCalls = candidate.ToolCalls;
            iteration.InputTokens = candidate.InputTokens;
            iteration.OutputTokens = candidate.OutputTokens;

            // accepted Iteration 永不退还预算；即使 epoch 已变化也要结算计数，
            // 但旧 epoch 绝不能创建 continuation 或改变当前 phase。
            if (goal.IterationsSettled < goal.IterationsStarted)
                goal.IterationsSettled++;
            goal.ActiveElapsedMs += candidate.ActiveElapsedMs;
            goal.TotalToolCalls += candidate.ToolCalls;
            goal.InputTokens += candidate.InputTokens;
            goal.OutputTokens += candidate.OutputTokens;
            goal.AggregateVersion++;
            goal.UpdatedAtUtc = now;

            var verificationId = $"gv-{goal.GoalRunId}-{iteration.ActivationEpoch}-{iteration.IterationNo}";
            db.GoalVerifications.Add(new GoalVerificationEntity
            {
                VerificationId = verificationId,
                GoalRunId = goal.GoalRunId,
                ActivationEpoch = iteration.ActivationEpoch,
                IterationNo = iteration.IterationNo,
                SourceTurnId = iteration.TurnId,
                SourceTerminalSequence = iteration.TerminalSequence,
                ContractVersion = 1,
                Status = "succeeded",
                Verdict = ToWire(decision.Verdict),
                Summary = decision.Reason,
                UnmetCriteriaJson = JsonSerializer.Serialize(decision.UnmetCriteria, JsonOpts),
                NextAction = decision.NextAction,
                BlockerCode = decision.BlockerCode,
                BlockerMessage = decision.BlockerMessage,
                EvidenceRefsJson = JsonSerializer.Serialize(decision.EvidenceRefs, JsonOpts),
                CreatedAtUtc = now,
                CompletedAtUtc = now,
            });
            goal.LastVerificationId = verificationId;
            goal.LastNextAction = decision.NextAction;
            // P0-2（ADR-092 §7）：进度记账 —— 三个连续计数器与进度指纹的唯一写入点（结算事务内）。
            // 等待族整轮跳过；指纹缺失视为证据不足（不奖不罚）。语义详见 ApplyProgressAccounting。
            var progressAccounting = ApplyProgressAccounting(goal, decision);

            // ── P0-3（ADR-092 §7.6）：不可恢复处置的连续同因降级门槛 ──
            // 单次不可恢复 verdict 不再直接终态化：仅当同一不可恢复原因连续达到
            // NoProgressBreakerThreshold（默认 3）才走原终态化路径；未达阈值时本轮降级为
            // 有界修复，并在 decision 与下方 ProgressRecorded 事件中记录“已连续 N 次 / 阈值 M”。
            // 安全与用户意图例外（立即终止，绝不降级）：Unsafe verdict / unsafe 码；
            // cancelled / iteration_cancelled（显式取消被 ADR-092 定为终态，GoalVerificationContracts
            // 的 UnrecoverableBlockerCodes 注释明文禁止降级）。判定口径见 EvaluateUnrecoverableDemotion。
            var unrecoverableDemotion = EvaluateUnrecoverableDemotion(goal, decision);
            if (unrecoverableDemotion.Demoted)
            {
                decision = decision with
                {
                    Reason = $"Unrecoverable blocker '{unrecoverableDemotion.Code}' observed " +
                             $"{unrecoverableDemotion.Consecutive}/{_noProgressBreakerThreshold} consecutive settlements; demoted to " +
                             "bounded repair for this settlement (terminal escalation deferred until the threshold is reached).",
                };
            }

            var events = new List<GoalEventDraft>
            {
                new(GoalEventTypes.IterationSettled, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    turnId = iteration.TurnId,
                    terminalSequence = iteration.TerminalSequence,
                    terminalKind = turn.TerminalKind,
                }),
                new(GoalEventTypes.VerificationRequested, GoalProducerComponents.Verifier, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    verificationId,
                }),
                new(GoalEventTypes.VerificationCompleted, GoalProducerComponents.Verifier, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    verificationId,
                    verdict = ToWire(decision.Verdict),
                    evidenceRefs = decision.EvidenceRefs,
                }),
                // P0-2：进度记账审计事件（事件目录 §12.5 预留的 goal.progress.recorded，首次接线启用）。
                new(GoalEventTypes.ProgressRecorded, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    waitExcluded = progressAccounting.WaitExcluded,
                    fingerprintChanged = progressAccounting.FingerprintChanged,
                    consecutiveNoProgress = goal.ConsecutiveNoProgress,
                    consecutiveSameBlocker = goal.ConsecutiveSameBlocker,
                    consecutiveInfraFailures = goal.ConsecutiveInfraFailures,
                    progressFingerprint = goal.LastProgressFingerprint,
                    // P0-3：不可恢复降级审计（复用既有事件，载荷向后兼容追加字段）。
                    unrecoverableDemoted = unrecoverableDemotion.Demoted,
                    unrecoverableCode = unrecoverableDemotion.Code,
                    unrecoverableConsecutive = unrecoverableDemotion.Consecutive,
                    unrecoverableThreshold = _noProgressBreakerThreshold,
                }),
            };

            var currentEpoch = goal.Status == GoalPhase.Active
                && goal.ActivationEpoch == iteration.ActivationEpoch;
            if (currentEpoch)
            {
                // 结算可能被重放（worker 重扫 / at-least-once 投递）：同一 (goal, epoch, iteration)
                // 的 continuation outbox 行只允许存在一条，否则重复 Add 会撞
                // UX_goal_outbox_idempotency_key，让整个结算事务失败（真实运行已复现）。
                var queuedContinuations = await db.GoalOutbox
                    .Where(item => item.GoalRunId == goal.GoalRunId
                        && item.ActivationEpoch == goal.ActivationEpoch
                        && item.Kind == GoalOutboxValues.Continuation)
                    .Select(item => item.OutboxId)
                    .ToListAsync(ct);
                ApplyCurrentVerdict(
                    db,
                    goal,
                    binding,
                    task,
                    iteration,
                    decision,
                    boundPlan,
                    now,
                    events,
                    queuedContinuations,
                    ref nextContinuation);
            }

            await AppendGoalEventsAsync(db, goal, iteration, events, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            signalledConversation = goal.CurrentConversationId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        if (signalledConversation is not null)
            committedSignal.Signal(signalledConversation, -1);
        if (nextContinuation)
            outboxSignal.Signal();
        return true;
    }

    private static GoalVerificationDecision ApplyDeterministicGates(
        GoalVerificationDecision proposed,
        WorkspaceTaskEntity? task,
        GoalSettlementCandidate candidate)
    {
        if (!candidate.EvidenceComplete || candidate.HasPendingExecutionFacts)
        {
            return BlockedDecision(
                "evidence_incomplete",
                "Canonical terminal evidence is incomplete or still pending.",
                candidate.EvidenceRefs);
        }
        if (!string.Equals(candidate.TerminalKind, "completed", StringComparison.OrdinalIgnoreCase))
        {
            // Passthrough the authoritative errorCode/errorMessage from the terminal
            // turn.failed payload so the archived Goal record (blocked_code / blocked_message)
            // and the bound Task (blocker_kind / blocker_reason) surface the real root cause
            // instead of the generic iteration_failed marker.
            var blockedCode = $"iteration_{candidate.TerminalKind}";
            var blockedMessage = $"Iteration ended as {candidate.TerminalKind}.";
            if (string.Equals(candidate.TerminalKind, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.ErrorCode))
            {
                blockedCode = candidate.ErrorCode;
                blockedMessage = string.IsNullOrWhiteSpace(candidate.ErrorMessage)
                    ? $"Iteration failed: {candidate.ErrorCode}."
                    : candidate.ErrorMessage;
            }

            return BlockedDecision(blockedCode, blockedMessage, candidate.EvidenceRefs);
        }
        // ADR-092 §6.2（G92-1 P3）：Task 终态不再是"完成"的前提，而是"不能完成"的否决项。
        // 完成是否可达由 ApplyBoundPlanGates 依据"无剩余 WorkUnit + 必需条件全通过"独立判定；
        // 这里只拦下绑定 Task 已进入必须外部处理才能继续的终态的情况——
        // 在 Blocked/Failed/Cancelled/NeedsReview 之上宣告 Goal 完成会掩盖仍未处理的失败/阻塞事实。
        if (proposed.Verdict == GoalVerificationVerdict.Complete
            && IsTaskTerminalNonCompletable(task?.Status))
        {
            return BlockedDecision(
                "task_blocked",
                $"The bound Task is {task?.Status} and still requires recovery; the Goal cannot be completed over it.",
                candidate.EvidenceRefs);
        }
        return proposed with { EvidenceRefs = candidate.EvidenceRefs };
    }

    /// <summary>
    /// ADR-092 §6.2（G92-1 P3）：绑定 Task 处于必须由用户/复核者/修复通道处理的终态时，
    /// 整体完成一律否决。未绑定 Task（null）或仍可继续推进的状态不构成完成障碍。
    /// </summary>
    private static bool IsTaskTerminalNonCompletable(WorkspaceTaskStatus? status) => status is
        WorkspaceTaskStatus.Blocked
        or WorkspaceTaskStatus.Failed
        or WorkspaceTaskStatus.Cancelled
        or WorkspaceTaskStatus.NeedsReview;

    private static async Task<BoundPlanState?> LoadBoundPlanAsync(
        PlatformDbContext db,
        TaskGoalBindingEntity? binding,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(binding?.TaskPlanId))
            return null;

        var plan = await db.TaskPlanRuns.SingleOrDefaultAsync(
            item => item.PlanId == binding.TaskPlanId,
            ct);
        if (plan is null)
            return BoundPlanState.Invalid("The bound execution plan no longer exists.");
        if (!string.Equals(plan.PlanFingerprint, binding.PlanFingerprint, StringComparison.Ordinal))
            return BoundPlanState.Invalid("The bound execution plan fingerprint no longer matches the Task binding.");
        if (!string.Equals(plan.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(plan.WorkspaceTaskId, binding.TaskId, StringComparison.Ordinal)
            || !string.Equals(plan.LeaderAgentId, binding.AgentInstanceId, StringComparison.Ordinal))
        {
            return BoundPlanState.Invalid("The bound execution plan ownership no longer matches the Task binding.");
        }

        var nodes = await db.TaskNodes
            .Where(item => item.PlanId == plan.PlanId)
            .OrderBy(item => item.Depth)
            .ThenBy(item => item.SequenceNo)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        // ADR-092 §13.1（G92-1 刀 C）：结构判定与状态判定必须分开——"0 个 Running"不是错误的充分条件。
        // 只有 root 缺失/重复、多 Running 违反串行计划约束这类真正不可恢复的结构问题才 Invalid；
        // 收敛态（0 Running 且必需单元全部已验证完成）与"0 Running 但仍有必需单元"都必须被接受。
        var roots = nodes.Where(item => item.Depth == 0).ToList();
        if (roots.Count != 1)
            return BoundPlanState.Invalid($"The bound execution plan must have exactly one root; roots={roots.Count}.");

        var units = nodes.Where(item => item.Depth == 1).ToList();
        if (units.Count == 0)
            return BoundPlanState.Invalid("The bound execution plan has no WorkUnit node to verify.");

        var runningUnits = units
            .Where(item => string.Equals(item.Status, TaskNodeStatuses.Running.ToString(), StringComparison.Ordinal))
            .ToList();
        if (runningUnits.Count > 1)
        {
            return BoundPlanState.Invalid(
                $"The bound execution plan must run its WorkUnits serially; running={runningUnits.Count}.");
        }

        var planNodeIds = nodes.Select(item => item.TaskNodeId).ToHashSet(StringComparer.Ordinal);
        var requiredUnits = units
            .Where(item => !IsExplicitlyReplaced(item, planNodeIds))
            .ToList();
        if (requiredUnits.Count == 0)
        {
            return BoundPlanState.Invalid(
                "Every WorkUnit of the bound execution plan was replaced without a required successor.");
        }

        // 剩余必需工作数 = 本次裁决后仍未验证完成的必需单元数；当前 Running 单元也在其中，
        // 它只有在真实验收通过后才从剩余数里扣除（§13.1），所以"有 Current"不等于"已完成"。
        var openRequiredUnits = requiredUnits
            .Where(item => !IsUnitVerifiedComplete(item, nodes, planNodeIds))
            .ToList();
        var current = runningUnits.Count == 1 ? runningUnits[0] : null;
        // 推进/认领目标 = 第一个仍未完成的必需单元（不再只看 SequenceNo 更大的节点，
        // 否则更早序号的未完成节点会被漏掉，制造假的整体完成）。
        var next = openRequiredUnits
            .Where(item => !ReferenceEquals(item, current))
            .OrderBy(item => item.SequenceNo)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        return new BoundPlanState(plan, roots[0], current, next, null)
        {
            RemainingRequiredUnits = openRequiredUnits.Count,
            RunningUnitCount = runningUnits.Count,
            // 0 Running 时区分"有 ready（Draft/Planned/Assigned）⇒ 认领推进"与"其余 ⇒ typed wait"。
            ReadyUnitAvailable = next is not null
                && (next.Status is "Draft" or "Planned" or "Assigned"),
        };
    }

    /// <summary>
    /// ADR-092 §13.1（G92-1 刀 C）：合法计划修订的显式替代——只有 Superseded 且后继节点确实存在于
    /// 同一计划内，该节点的义务才由后继承担；Cancelled 与"无后继的 Superseded"不自动等于义务消失，
    /// 仍计入必需集合（宁可 fail-closed，也不冒充"没有剩余工作"）。
    /// </summary>
    private static bool IsExplicitlyReplaced(TaskNodeEntity node, HashSet<string> planNodeIds)
        => string.Equals(node.Status, TaskNodeStatuses.Superseded.ToString(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(node.SupersededByTaskNodeId)
            && planNodeIds.Contains(node.SupersededByTaskNodeId);

    /// <summary>
    /// ADR-092 §13.1（G92-1 刀 C）：一个必需单元只有"自身与必需后代全部 Completed"才算验证完成。
    /// 后代按 ParentTaskNodeId 展开（迭代 + visited 防环）；同层 DependsOnJson 只是顺序约束，
    /// 由节点状态（Blocked）与 typed wait 表达，不在剩余计数里重复计算义务。
    /// </summary>
    private static bool IsUnitVerifiedComplete(
        TaskNodeEntity unit,
        IReadOnlyList<TaskNodeEntity> nodes,
        HashSet<string> planNodeIds)
    {
        if (!string.Equals(unit.Status, TaskNodeStatuses.Completed.ToString(), StringComparison.Ordinal))
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal) { unit.TaskNodeId };
        var pending = new Queue<TaskNodeEntity>(
            nodes.Where(item => string.Equals(item.ParentTaskNodeId, unit.TaskNodeId, StringComparison.Ordinal)));
        while (pending.Count > 0)
        {
            var descendant = pending.Dequeue();
            if (!visited.Add(descendant.TaskNodeId))
                continue; // 防环：图损坏时也不得死循环（结构问题由 root/多 Running 判定，这里不臆断）
            if (IsExplicitlyReplaced(descendant, planNodeIds))
                continue;
            if (!string.Equals(descendant.Status, TaskNodeStatuses.Completed.ToString(), StringComparison.Ordinal))
                return false;
            foreach (var child in nodes.Where(
                item => string.Equals(item.ParentTaskNodeId, descendant.TaskNodeId, StringComparison.Ordinal)))
            {
                pending.Enqueue(child);
            }
        }

        return true;
    }

    /// <summary>
    /// ADR-092 §13.1（G92-1 刀 C）：把绑定执行计划折算成 verifier 需要的作用域与剩余必需工作数。
    /// 这里是真实计数（按同一版本的全部必需 depth1 节点及其完成所依赖的必需后代核验），
    /// 不再是"有没有下一个节点 / Root、Current 是否齐备"的布尔近似：
    /// 全部必需单元已验证完成 ⇒ (goal, 0)——合法收敛态，此时没有 Current；
    /// 尚有必需单元未完成 ⇒ (work_unit, 剩余必需数)；计划缺失、指纹/归属不匹配、结构非法或
    /// 必需性无法判定 ⇒ (work_unit, null)。未知绝不得冒充 0，否则直接制造假的整体完成。
    /// </summary>
    private static (string Scope, int? RemainingWorkUnits) ResolveVerificationScope(BoundPlanState? plan)
    {
        if (plan is not { Error: null, Plan: not null, Root: not null }
            || plan.RemainingRequiredUnits is not int remaining)
        {
            return (GoalVerificationScopes.WorkUnit, null);
        }

        return remaining == 0
            ? (GoalVerificationScopes.Goal, 0)
            : (GoalVerificationScopes.WorkUnit, remaining);
    }

    private static GoalVerificationDecision ApplyBoundPlanGates(
        GoalVerificationDecision decision,
        GoalSettlementCandidate candidate,
        BoundPlanState? plan)
    {
        if (plan is null)
            return decision;
        if (plan.Error is not null)
        {
            return BlockedDecision(
                "task_plan_state_invalid",
                plan.Error,
                candidate.EvidenceRefs);
        }
        if (!string.Equals(candidate.TerminalKind, "completed", StringComparison.OrdinalIgnoreCase)
            || decision.Verdict is GoalVerificationVerdict.Blocked
                or GoalVerificationVerdict.NeedsUser
                or GoalVerificationVerdict.Unsafe)
        {
            return decision;
        }

        // ADR-092 §4/§6.1（G92-0）：Turn 结束、Task Completed 都不是验收证据。
        // 只有当前单元的必需条件通过真实检查，才允许推进到下一单元或完成目标。
        var verifiedAcceptance = GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision);

        if (plan.Current is null)
        {
            // ADR-092 §13.1（G92-1 刀 C）四分流之②③：0 个 Running 不是错误的充分条件，
            // 无 Current 的合法收敛态必须被接受，而不是当成结构错误或空指针。
            if (plan.RemainingRequiredUnits == 0)
            {
                // ② 0 Running 且全部必需单元已验证完成：合法收敛态——进入/恢复 VerifyGoal（待整体验证）。
                if (GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision))
                {
                    // 整体（goal 作用域）必需条件已有同版本 passed 证据：本次裁决即可完成；
                    // 最后一个 Running 单元也正是在这类结算里被一次验证并完成，无需先用 task 工具置 Completed。
                    return decision with
                    {
                        Verdict = GoalVerificationVerdict.Complete,
                        EvidenceRefs = candidate.EvidenceRefs,
                    };
                }

                // 整体验证证据尚未齐备：保持 Goal/Plan 非终态，调度尚缺的整体验证。
                return decision with
                {
                    Verdict = GoalVerificationVerdict.Continue,
                    Reason = $"The bound execution plan has converged (running={plan.RunningUnitCount}, remainingRequiredWorkUnits={plan.RemainingRequiredUnits}), but the goal-scope required criteria have no passing verification yet; the goal stays open pending overall verification.",
                    NextAction = plan.Root?.Objective,
                    EvidenceRefs = candidate.EvidenceRefs,
                    BlockerCode = decision.BlockerCode ?? "acceptance_not_verified",
                    BlockerMessage = "Goal-scope required criteria are not verified yet.",
                };
            }

            // ③ 0 Running 但仍有必需单元：有 ready ⇒ 认领推进；等依赖 ⇒ typed wait。
            // 既不得误 Complete，也不得单凭 running=0 判 Failed。
            if (plan.Next is null)
            {
                // 计数说还有必需单元、却没有可认领目标：计划与计数不自洽，按结构问题 fail-closed。
                return BlockedDecision(
                    "task_plan_state_invalid",
                    "The bound execution plan reports remaining required WorkUnits but exposes no claimable WorkUnit node.",
                    candidate.EvidenceRefs);
            }

            if (plan.ReadyUnitAvailable)
            {
                return decision with
                {
                    Verdict = GoalVerificationVerdict.Continue,
                    Reason = "No WorkUnit is running; the remaining required WorkUnits are ready to be claimed.",
                    NextAction = plan.Next.Objective,
                    EvidenceRefs = candidate.EvidenceRefs,
                };
            }

            return new GoalVerificationDecision
            {
                Verdict = GoalVerificationVerdict.Blocked,
                Reason = "No WorkUnit is running; the remaining required WorkUnits are waiting for their dependencies (or for a legal plan revision that removes/replaces them).",
                EvidenceRefs = candidate.EvidenceRefs,
                // T5：保留 verifier 从真实检查报告得出的未满足清单——gate 换裁决不得丢弃真实证据。
                UnmetCriteria = decision.UnmetCriteria,
                NextAction = plan.Next.Objective,
                BlockerCode = "dependency_wait",
                BlockerMessage = "The bound execution plan has no running WorkUnit and no claimable WorkUnit node.",
            };
        }

        if (plan.Next is not null)
        {
            return decision with
            {
                Verdict = GoalVerificationVerdict.Continue,
                Reason = decision.Verdict == GoalVerificationVerdict.Complete
                    ? (verifiedAcceptance
                        ? "Task completion was deferred because the bound execution plan still has WorkUnits to run."
                        : "Task completion was deferred: the current WorkUnit has no verified acceptance for its required criteria, and the bound plan still has WorkUnits to run.")
                    : decision.Reason,
                NextAction = verifiedAcceptance ? plan.Next.Objective : plan.Current.Objective,
                EvidenceRefs = candidate.EvidenceRefs,
                BlockerCode = verifiedAcceptance ? decision.BlockerCode : decision.BlockerCode ?? "acceptance_not_verified",
                BlockerMessage = verifiedAcceptance
                    ? decision.BlockerMessage
                    : "The current WorkUnit's required criteria have no passing verification result.",
                // 作用域与剩余必需数是绑定计划/capsule 的事实：裁决记录不重复携带，避免第二份真值（刀 C）。
            };
        }

        // ADR-092 §5.2/§6.2（G92-1 P3）：完成的唯一判据是"计划已收敛（无剩余 WorkUnit）
        // 且 Goal 的全部必需条件都有同版本 passed 的检查结果"。Task.Status 不再是前置条件——
        // canonical 完成由结算事务统一写入（G92-1 刀 B），此前"Task 必须先完成"的要求
        // 与"唯一合法完成通道会释放本轮要重验的 reservation"互锁，使完成永不可达。
        if (GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision))
        {
            return decision with
            {
                Verdict = GoalVerificationVerdict.Complete,
                EvidenceRefs = candidate.EvidenceRefs,
                // 无剩余必需 WorkUnit + 全部必需条件通过：唯一满足 goal 作用域完成的组合，
                // 也是唯一允许原子结束 Goal/Plan/Task 的路径（刀 C：最后一个 Running 单元在本次结算内一并验证并完成）。
            };
        }

        // 最后一个 WorkUnit 已跑完，但必需条件尚无通过证据：保留 Goal 打开，回到当前单元继续取证。
        // 必须用 `with` 保留 Criteria/CriterionResults——旧的 task_completion_fact_missing 分支
        // 会换成只有 blocker 的新裁决，把本单元已验证的单元级证据一起丢掉。
        return decision with
        {
            Verdict = GoalVerificationVerdict.Continue,
            Reason = "The final WorkUnit has no remaining WorkUnit to run, but the goal's required criteria have no passing verification; the goal stays open.",
            NextAction = plan.Current.Objective,
            EvidenceRefs = candidate.EvidenceRefs,
            BlockerCode = decision.BlockerCode ?? "acceptance_not_verified",
            BlockerMessage = "Required criteria are not verified yet.",
        };
    }

    private static void ApplyBoundPlanVerdict(
        BoundPlanState? plan,
        GoalIterationEntity iteration,
        GoalVerificationDecision decision,
        DateTimeOffset now,
        string? dispositionOverride = null)
    {
        // ADR-092 §13.1（G92-1 刀 C）：只有结构错误不可写；无 Current 的合法收敛态
        // （0 Running 且必需单元全部已验证完成）同样必须被写入，否则 Plan/Root 永远无法随 Goal 收口。
        if (plan is null || plan.Error is not null || plan.Plan is null || plan.Root is null)
            return;

        var nowMs = now.ToUnixTimeMilliseconds();

        // ADR-092 §6.1（G92-0）：只有当前单元的必需条件通过真实检查，才允许把单元/计划置为完成。
        // StopReason=completed、Turn 结束、Task Completed、"最后的 WorkUnit 缺 Task 事实" 都不是验收证据。
        var verifiedWorkUnit = string.Equals(iteration.StopReason, "completed", StringComparison.Ordinal)
            && GoalSettlementDecisionCalculator.HasVerifiedAcceptance(decision);
        // P0-3：降级轮的原始不可恢复码仍会算出 Stop，由调用方显式降级为 repair ——
        // 绑定计划不得随单次不可恢复 verdict 终结。
        var disposition = dispositionOverride
            ?? GoalSettlementDecisionCalculator.ComputeDisposition(decision);

        if (verifiedWorkUnit)
        {
            if (plan.Current is not null)
            {
                plan.Current.Status = TaskNodeStatuses.Completed.ToString();
                plan.Current.ResultSummary = decision.Reason;
                plan.Current.ResultArtifactRef =
                    $"conversation-turn:{iteration.TurnId}:terminal:{iteration.TerminalSequence}";
                plan.Current.ProgressFingerprint = decision.ProgressFingerprint;
                plan.Current.CompletedAt ??= nowMs;
                plan.Current.UpdatedAt = nowMs;
            }

            if (decision.Verdict == GoalVerificationVerdict.Complete
                && GoalSettlementDecisionCalculator.AllRequiredCriteriaPassed(decision))
            {
                plan.Plan.Status = TaskPlanStatuses.Completed.ToString();
                plan.Plan.ResultSummary = decision.Reason;
                plan.Plan.CompletedAt ??= nowMs;
                plan.Root.Status = TaskNodeStatuses.Completed.ToString();
                plan.Root.ResultSummary = decision.Reason;
                plan.Root.CompletedAt ??= nowMs;
                plan.Root.UpdatedAt = nowMs;
            }

            plan.Plan.UpdatedAt = nowMs;
            return;
        }

        // 未验证完成：保留当前单元的执行身份与计划，不把 Plan/Root 置为 Failed。
        // 收敛态（无 Current）下没有可回写的单元，只保留 Goal/Plan 非终态与阻塞事实。
        if (plan.Current is not null)
        {
            plan.Current.ErrorMessage = decision.BlockerMessage ?? decision.Reason;
            plan.Current.UpdatedAt = nowMs;
        }

        // 只有不可恢复处置才终止计划；等待依赖/需要修复/需要人工决定都不得关闭整个 Goal。
        if (string.Equals(disposition, GoalSettlementDispositions.Stop, StringComparison.Ordinal))
        {
            if (plan.Current is not null)
            {
                plan.Current.Status = TaskNodeStatuses.Failed.ToString();
                plan.Current.CompletedAt ??= nowMs;
            }
            plan.Plan.Status = TaskPlanStatuses.Failed.ToString();
            plan.Plan.ErrorMessage = decision.BlockerMessage ?? decision.Reason;
            plan.Plan.CompletedAt ??= nowMs;
            plan.Plan.UpdatedAt = nowMs;
            plan.Root.Status = TaskNodeStatuses.Failed.ToString();
            plan.Root.ErrorMessage = decision.BlockerMessage ?? decision.Reason;
            plan.Root.CompletedAt ??= nowMs;
            plan.Root.UpdatedAt = nowMs;
            return;
        }

        plan.Plan.UpdatedAt = nowMs;
    }

    private void ApplyCurrentVerdict(
        PlatformDbContext db,
        GoalRunEntity goal,
        TaskGoalBindingEntity? binding,
        WorkspaceTaskEntity? task,
        GoalIterationEntity iteration,
        GoalVerificationDecision decision,
        BoundPlanState? boundPlan,
        DateTimeOffset now,
        List<GoalEventDraft> events,
        IReadOnlyCollection<string> queuedContinuations,
        ref bool nextContinuation)
    {
        // P0-3（ADR-092 §7.6）：与主流程同一确定性降级判定（基于同一份记账后状态，结果一致）。
        // 降级轮：计划回写与终态化分支都按 repair 处理 —— 未达阈值时 Goal/Task/binding 与绑定计划
        // 均不因单次不可恢复 verdict 终结；降级事实由主流程的 ProgressRecorded 事件与 decision 承载。
        var unrecoverableDemotion = EvaluateUnrecoverableDemotion(goal, decision);

        ApplyBoundPlanVerdict(
            boundPlan,
            iteration,
            decision,
            now,
            unrecoverableDemotion.Demoted ? GoalSettlementDispositions.Repair : null);

        if (decision.Verdict == GoalVerificationVerdict.Complete)
        {
            goal.Status = GoalPhase.Completed;
            goal.StatusReason = decision.Reason;
            goal.TerminalAtUtc = now;
            goal.ActivationEpoch++;
            events.Add(new(GoalEventTypes.Completed, GoalProducerComponents.Coordinator, VerdictPayload(goal, iteration, decision)));
            if (binding is not null)
            {
                binding.Status = "terminal";
                binding.ReleasedAtUtc = now;
                events.Add(new(GoalEventTypes.TaskGoalCompleted, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    taskId = binding.TaskId,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                }));
                ReleaseReservation(db, binding, now, "goal_completed");
                // ADR-092 §6.2（G92-1 刀 B / P4）：完成口统一——把绑定 Task 的 canonical 终态写在
                // 本结算事务内（同一个 db + 单次 SaveChanges），随后沿用既有释放序列
                // assignment → attempt → reservation → binding。Task 完成只此一处写入。
                if (task is not null)
                    CompleteBoundTask(db, binding, task, now, goal.GoalRunId);
            }
            return;
        }

        // ADR-092 §6.1：Blocked/NeedsUser/Unsafe 的具体含义必须由 typed disposition 决定；
        // Repair/ContinueCurrent/Wait 都是可恢复的，不得把 Goal 置为终态。
        // P0-3：未达降级阈值时短路为 repair（ComputeDisposition 对原始码仍会返回 Stop）。
        var typedDisposition = unrecoverableDemotion.Demoted
            ? GoalSettlementDispositions.Repair
            : GoalSettlementDecisionCalculator.ComputeDisposition(decision);

        // ── P0-2（ADR-092 §7，对齐 codex-rs ext/goal accounting）：无进展/同阻塞熔断接线 ──
        // 任一连续计数器达到阈值后，本结算不再返回 repair：先尝试一次 Replan（提升绑定计划
        // PlanVersion、退回卡死单元、改选另一 ready WorkUnit），Replan 后按 typed wait 收口；
        // Replan 不可行或本 episode 的一次性 Replan 已消耗（任一计数器已越过阈值）⇒ 转 needs_user。
        // 熔断绝不静默：原因与连续计数落 goal 字段（BlockedCode/StatusReason）与
        // goal.circuit_opened 事件（含连续计数、指纹、阻塞码），始终落在可人工处理的状态。
        if (typedDisposition == GoalSettlementDispositions.Repair
            && NoProgressBreakerTripped(goal))
        {
            var replanApplied = !NoProgressBreakerExceeded(goal)
                && TryReplanBoundPlan(boundPlan, goal, decision, now, events);
            if (replanApplied)
            {
                // 一次性改道：给 Replan 后的新路径一个有界冷却窗口（typed wait，续行 due +1min）。
                typedDisposition = GoalSettlementDispositions.Wait;
                decision = decision with
                {
                    Reason = "No-progress circuit breaker opened; a one-shot replan demoted the stuck WorkUnit and selected the next ready WorkUnit.",
                    BlockerCode = NoProgressCircuitOpenBlockerCode,
                    BlockerMessage = $"Circuit breaker opened after {goal.ConsecutiveNoProgress} settlements without progress " +
                                     $"(sameBlocker={goal.ConsecutiveSameBlocker}, infraFailures={goal.ConsecutiveInfraFailures}); one replan applied.",
                };
            }
            else
            {
                typedDisposition = GoalSettlementDispositions.NeedsUser;
                decision = decision with
                {
                    Verdict = GoalVerificationVerdict.NeedsUser,
                    Reason = "No-progress circuit breaker opened; replan is unavailable or already spent for this episode.",
                    BlockerCode = NoProgressCircuitOpenBlockerCode,
                    BlockerMessage = $"Circuit breaker opened after {goal.ConsecutiveNoProgress} settlements without progress " +
                                     $"(sameBlocker={goal.ConsecutiveSameBlocker}, infraFailures={goal.ConsecutiveInfraFailures}, " +
                                     $"threshold={_noProgressBreakerThreshold}); human decision required.",
                };
                events.Add(new(GoalEventTypes.CircuitOpened, GoalProducerComponents.Coordinator, new
                {
                    kind = "needs_user",
                    goalRunId = goal.GoalRunId,
                    activationEpoch = iteration.ActivationEpoch,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                    threshold = _noProgressBreakerThreshold,
                    consecutiveNoProgress = goal.ConsecutiveNoProgress,
                    consecutiveSameBlocker = goal.ConsecutiveSameBlocker,
                    consecutiveInfraFailures = goal.ConsecutiveInfraFailures,
                    progressFingerprint = goal.LastProgressFingerprint,
                    blockerCode = decision.BlockerCode,
                }));
            }
        }

        var recoverableOutcome = typedDisposition is GoalSettlementDispositions.Repair
            or GoalSettlementDispositions.ContinueCurrent
            or GoalSettlementDispositions.Wait;

        if (!recoverableOutcome
            && (decision.Verdict is GoalVerificationVerdict.Blocked
                or GoalVerificationVerdict.NeedsUser
                or GoalVerificationVerdict.Unsafe))
        {
            // A standalone Goal remains resumable while blocked. A Task-bound Goal,
            // however, releases its binding, reservation and assignment below so a
            // later Task Resume/Requeue can create a fresh fenced attempt. Leaving
            // that detached Goal in the non-terminal Blocked phase violates the
            // (conversation, agent) active-Goal invariant and makes every retry hit
            // UX_goal_runs_active. The Task remains Blocked/NeedsReview, while this
            // particular execution attempt becomes an auditable Failed terminal Goal.
            var taskBoundAttempt = binding is not null;
            goal.Status = taskBoundAttempt ? GoalPhase.Failed : GoalPhase.Blocked;
            goal.BlockedCode = decision.BlockerCode ?? ToWire(decision.Verdict);
            goal.BlockedMessage = decision.BlockerMessage ?? decision.Reason;
            goal.StatusReason = decision.Reason;
            if (taskBoundAttempt)
                goal.TerminalAtUtc = now;
            goal.ActivationEpoch++;
            events.Add(new(
                taskBoundAttempt ? GoalEventTypes.Failed : GoalEventTypes.Blocked,
                GoalProducerComponents.Coordinator,
                VerdictPayload(goal, iteration, decision)));
            // 不可恢复的尝试终结：绑定计划必须随之失败，否则计划会留在 Running，
            // 与"本次尝试已终结"的 Goal 事实自相矛盾（真实运行已复现 plan 仍为 Running）。
            FailIncompleteBoundPlan(boundPlan, goal.BlockedCode ?? decision.Reason, now);
            if (binding is not null)
            {
                var completionFactMissing = string.Equals(
                    decision.BlockerCode,
                    "task_completion_fact_missing",
                    StringComparison.Ordinal);
                var taskChanged = false;
                var taskBlocked = false;
                if (task is not null
                    && completionFactMissing
                    && TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.NeedsReview))
                {
                    task.Status = WorkspaceTaskStatus.NeedsReview;
                    task.BlockerKind = decision.BlockerCode;
                    task.BlockerReason = goal.BlockedMessage;
                    taskChanged = true;
                }
                else if (task is not null
                    && TaskStateMachine.CanTransition(task.Status, WorkspaceTaskStatus.Blocked))
                {
                    task.Status = WorkspaceTaskStatus.Blocked;
                    task.BlockerKind = goal.BlockedCode;
                    task.BlockerReason = goal.BlockedMessage;
                    taskChanged = true;
                    taskBlocked = true;
                }
                events.Add(new(GoalEventTypes.TaskGoalBlocked, GoalProducerComponents.Coordinator, new
                {
                    goalRunId = goal.GoalRunId,
                    taskId = binding.TaskId,
                    blockerCode = goal.BlockedCode,
                    aggregateVersion = goal.AggregateVersion,
                    iterationNumber = iteration.IterationNo,
                }));
                // The failed Goal remains as immutable audit history, but it is not an
                // executing lease. Explicit Task resume/requeue creates a fresh fenced
                // Goal and assignment without inheriting the failed attempt's budget.
                binding.Status = "terminal";
                binding.ReleasedAtUtc = now;
                ReleaseReservation(db, binding, now, "goal_blocked");
                if (task is not null)
                {
                    taskChanged |= ReleaseAssignment(
                        db,
                        binding,
                        task,
                        now,
                        AssignmentAttemptStatus.Failed);
                    if (taskChanged)
                    {
                        task.Version++;
                        task.UpdatedAtUtc = now;
                        AppendTaskEvent(
                            db,
                            task,
                            binding,
                            taskBlocked ? TaskEventType.TaskBlocked : TaskEventType.TaskUpdated,
                            now,
                            goal.GoalRunId);
                    }
                }
            }
            return;
        }

        if (recoverableOutcome)
        {
            // 可恢复的未通过（repair / continue_current / wait）：不得置 Failed、不得释放
            // binding/reservation/assignment，否则 Task-bound Goal 会在可恢复场景下结束目标并丢失执行租约。
            // 逻辑 Goal 保持 Active（下一步续行必须能再次结算），同时记录本轮阻塞事实；
            // 之后由下方 outbox 投递同一单元的下一次尝试。
            goal.BlockedCode = decision.BlockerCode ?? ToWire(decision.Verdict);
            goal.BlockedMessage = decision.BlockerMessage ?? decision.Reason;
            goal.StatusReason = decision.Reason;
        }

        if (GoalStateMachine.IsBudgetExhausted(goal.MaxIterations, goal.IterationsStarted))
        {
            FailIncompleteBoundPlan(boundPlan, "Goal accepted-iteration budget exhausted.", now);
            goal.Status = GoalPhase.BudgetExhausted;
            goal.StatusReason = "accepted_iteration_budget_exhausted";
            goal.TerminalAtUtc = now;
            goal.ActivationEpoch++;
            events.Add(new(GoalEventTypes.BudgetExhausted, GoalProducerComponents.Coordinator, VerdictPayload(goal, iteration, decision)));
            if (binding is not null)
            {
                binding.Status = "terminal";
                binding.ReleasedAtUtc = now;
                ReleaseReservation(db, binding, now, "goal_budget_exhausted");
                if (task is not null
                    && ReleaseAssignment(db, binding, task, now, AssignmentAttemptStatus.Failed))
                {
                    task.Version++;
                    task.UpdatedAtUtc = now;
                    AppendTaskEvent(db, task, binding, TaskEventType.TaskUpdated, now, goal.GoalRunId);
                }
            }
            return;
        }

        if (binding is not null && task is not null)
        {
            // Freeze the exact Task revision that the next synthetic acceptance
            // must revalidate. Task tool writes made during this iteration are
            // therefore included, while later external edits fence the outbox.
            binding.ExpectedTaskVersion = task.Version;
            RenewReservation(db, binding, now);
        }

        var nextIteration = goal.IterationsStarted + 1;
        var outboxId = $"gc-{goal.GoalRunId}-{goal.ActivationEpoch}-{nextIteration}";
        if (queuedContinuations.Contains(outboxId))
        {
            // 该次 continuation 已登记（重复结算 / worker 重扫）：复用既有行，
            // 不重复插入、不重复落事件，也不额外消耗迭代预算。
            nextContinuation = true;
            return;
        }

        db.GoalOutbox.Add(new GoalOutboxEntity
        {
            OutboxId = outboxId,
            GoalRunId = goal.GoalRunId,
            ActivationEpoch = goal.ActivationEpoch,
            AggregateVersion = goal.AggregateVersion,
            Kind = GoalOutboxValues.Continuation,
            IdempotencyKey = outboxId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                goalRunId = goal.GoalRunId,
                objectiveVersion = goal.ObjectiveVersion,
                iterationNo = nextIteration,
                taskId = binding?.TaskId,
                expectedTaskVersion = binding?.ExpectedTaskVersion,
                reservationFencingToken = binding?.ReservationFencingToken,
            }, JsonOpts),
            Status = GoalOutboxValues.Pending,
            // Wait 必须留下真实的恢复来源：带未来 due time 的持久 outbox，而不是永不到来的事件。
            DueAtUtc = recoverableOutcome
                && string.Equals(typedDisposition, GoalSettlementDispositions.Wait, StringComparison.Ordinal)
                    ? now.AddMinutes(1)
                    : now,
            CreatedAtUtc = now,
        });
        events.Add(new(GoalEventTypes.ContinuationRequested, GoalProducerComponents.Continuation, new
        {
            goalRunId = goal.GoalRunId,
            activationEpoch = goal.ActivationEpoch,
            aggregateVersion = goal.AggregateVersion,
            iterationNumber = nextIteration,
            remainingIterations = goal.MaxIterations - goal.IterationsStarted,
        }));
        nextContinuation = true;
    }

    /// <summary>P0-2：熔断落库的 blocker code（非终态、可人工处理；刻意不加入任何白名单）。</summary>
    private const string NoProgressCircuitOpenBlockerCode = "no_progress_circuit_open";

    /// <summary>P0-2：进度记账结果（goal.progress.recorded 审计事件载荷）。</summary>
    private sealed record GoalProgressAccounting(
        bool WaitExcluded,
        bool FingerprintChanged,
        bool SameBlockerCounted,
        bool InfraFailureCounted,
        bool SuccessReset);

    /// <summary>
    /// P0-2（ADR-092 §7）：结算事务内的进度记账 —— GoalRunEntity 三个连续计数器与
    /// last_progress_fingerprint 的唯一写入点。语义（逐条对应任务书 A）：
    /// ① 等待族（ComputeDisposition=wait，dependency_wait 等白名单，见
    ///    GoalSettlementDecisionCalculator.WaitBlockerCodes）整轮跳过：合法等待不是无进展；
    /// ② 指纹轴：新指纹缺失=证据不足（不动）；与上次相同 ⇒ ConsecutiveNoProgress++；
    ///    变化 ⇒ 归零并记录新指纹；
    /// ③ 阻塞轴：repair / stop（不可恢复，P0-3 降级门槛依赖同因轴）轮携带阻塞码 ⇒
    ///    与上次相同 ++、不同归零（ADR-092 §7「变化则归零」字面语义）；无阻塞码 ⇒ 归零；
    /// ④ 基础设施轴：repair 轮且阻塞码属 infra 族（租约/fence/存储证据）⇒
    ///    ConsecutiveInfraFailures++；成功（advance/complete）一次归零。
    /// </summary>
    private static GoalProgressAccounting ApplyProgressAccounting(
        GoalRunEntity goal,
        GoalVerificationDecision decision)
    {
        var typedDisposition = GoalSettlementDecisionCalculator.ComputeDisposition(decision);
        if (typedDisposition == GoalSettlementDispositions.Wait)
        {
            return new GoalProgressAccounting(
                WaitExcluded: true,
                FingerprintChanged: false,
                SameBlockerCounted: false,
                InfraFailureCounted: false,
                SuccessReset: false);
        }

        var fingerprintChanged = false;
        var nextFingerprint = decision.ProgressFingerprint;
        if (!string.IsNullOrWhiteSpace(nextFingerprint))
        {
            fingerprintChanged = !string.Equals(
                goal.LastProgressFingerprint, nextFingerprint, StringComparison.Ordinal);
            if (fingerprintChanged)
            {
                goal.ConsecutiveNoProgress = 0;
                goal.LastProgressFingerprint = nextFingerprint;
            }
            else
            {
                goal.ConsecutiveNoProgress++;
            }
        }

        var success = typedDisposition is GoalSettlementDispositions.Complete
            or GoalSettlementDispositions.Advance;
        if (success)
            goal.ConsecutiveInfraFailures = 0;

        // P0-3（ADR-092 §7.6）：Stop（不可恢复）轮与 Repair 轮同样计入同因轴 ——
        // “连续 N 次同一不可恢复原因”的降级门槛依赖这条连续计数；换成不同不可恢复码
        // 仍按“变化则归零”处理；其余处置（wait/needs_user 等）照旧归零。
        if (typedDisposition is not (GoalSettlementDispositions.Repair or GoalSettlementDispositions.Stop))
        {
            goal.ConsecutiveSameBlocker = 0;
            return new GoalProgressAccounting(
                WaitExcluded: false,
                FingerprintChanged: fingerprintChanged,
                SameBlockerCounted: false,
                InfraFailureCounted: false,
                SuccessReset: success);
        }

        var blocker = decision.BlockerCode;
        if (string.IsNullOrWhiteSpace(blocker))
        {
            goal.ConsecutiveSameBlocker = 0;
            return new GoalProgressAccounting(
                WaitExcluded: false,
                FingerprintChanged: fingerprintChanged,
                SameBlockerCounted: false,
                InfraFailureCounted: false,
                SuccessReset: success);
        }

        var sameAsLast = string.Equals(goal.BlockedCode, blocker, StringComparison.Ordinal);
        goal.ConsecutiveSameBlocker = sameAsLast ? goal.ConsecutiveSameBlocker + 1 : 0;

        var infra = GoalSettlementDecisionCalculator.IsInfraFailureBlockerCode(blocker);
        if (infra)
            goal.ConsecutiveInfraFailures++;

        return new GoalProgressAccounting(
            WaitExcluded: false,
            FingerprintChanged: fingerprintChanged,
            SameBlockerCounted: true,
            InfraFailureCounted: infra,
            SuccessReset: success);
    }

    /// <summary>P0-3：不可恢复降级判定结果。</summary>
    private sealed record UnrecoverableDemotion(bool Demoted, int Consecutive, string? Code);

    /// <summary>
    /// P0-3（ADR-092 §7.6）：“单次不可恢复 verdict ⇒ 终态化”的连续同因降级门槛。
    /// 仅当同一不可恢复原因连续达到 <see cref="_noProgressBreakerThreshold"/>（默认 3）才保持
    /// Stop（走原终态化路径）；未达阈值 ⇒ Demoted=true，本结算按 repair 收口。
    /// 计数口径：本判定在结算事务的进度记账（ApplyProgressAccounting）之后执行 ——
    /// goal.BlockedCode 是上一结算落库的阻塞码；ConsecutiveSameBlocker 是含本轮的记账后连续值
    /// （Stop 轮已纳入同因轴），故含本轮的连续次数 = 同码 ? 计数+1 : 1。
    /// 安全与用户意图例外（立即终止，绝不降级）：
    /// ① Unsafe verdict 或 blocker=unsafe —— 安全红线不可等 3 次；
    /// ② blocker=cancelled/iteration_cancelled —— 显式取消被 ADR-092 定为终态，
    ///    GoalVerificationContracts.UnrecoverableBlockerCodes 注释明文“不得降级为 repair 继续推进”。
    /// verdict=NeedsUser 是人工决定路径、不是不可恢复失败，同样不在降级范围（维持现状）。
    /// </summary>
    private UnrecoverableDemotion EvaluateUnrecoverableDemotion(
        GoalRunEntity goal,
        GoalVerificationDecision decision)
    {
        if (GoalSettlementDecisionCalculator.ComputeDisposition(decision)
            != GoalSettlementDispositions.Stop)
        {
            return new UnrecoverableDemotion(false, 0, null);
        }

        if (decision.Verdict == GoalVerificationVerdict.Unsafe
            || string.Equals(decision.BlockerCode, "unsafe", StringComparison.Ordinal))
        {
            return new UnrecoverableDemotion(false, 0, decision.BlockerCode);
        }

        if (decision.BlockerCode is "cancelled" or "iteration_cancelled")
        {
            return new UnrecoverableDemotion(false, 0, decision.BlockerCode);
        }

        var sameAsLast = string.Equals(goal.BlockedCode, decision.BlockerCode, StringComparison.Ordinal);
        var consecutive = (sameAsLast ? goal.ConsecutiveSameBlocker : 0) + 1;

        return new UnrecoverableDemotion(
            Demoted: consecutive < _noProgressBreakerThreshold,
            Consecutive: consecutive,
            Code: decision.BlockerCode);
    }

    /// <summary>P0-2：任一连续计数器达到熔断阈值。</summary>
    private bool NoProgressBreakerTripped(GoalRunEntity goal)
        => goal.ConsecutiveNoProgress >= _noProgressBreakerThreshold
           || goal.ConsecutiveSameBlocker >= _noProgressBreakerThreshold
           || goal.ConsecutiveInfraFailures >= _noProgressBreakerThreshold;

    /// <summary>P0-2：任一连续计数器已越过阈值 ⇒ 本 episode 的一次性 Replan 已消耗。</summary>
    private bool NoProgressBreakerExceeded(GoalRunEntity goal)
        => goal.ConsecutiveNoProgress > _noProgressBreakerThreshold
           || goal.ConsecutiveSameBlocker > _noProgressBreakerThreshold
           || goal.ConsecutiveInfraFailures > _noProgressBreakerThreshold;

    /// <summary>
    /// P0-2：熔断后的一次性 Replan —— 提升绑定计划 PlanVersion（以新版本身份重新调度），
    /// 把卡死的 Running WorkUnit 退回 Planned（保留 ErrorMessage 审计），由后续调度改选
    /// 另一 ready WorkUnit（boundPlan.Next）。没有绑定计划、计划结构非法、无卡死单元或
    /// 无另一 ready WorkUnit ⇒ Replan 不可行，返回 false（由调用方转 needs_user）。
    /// </summary>
    private bool TryReplanBoundPlan(
        BoundPlanState? boundPlan,
        GoalRunEntity goal,
        GoalVerificationDecision decision,
        DateTimeOffset now,
        List<GoalEventDraft> events)
    {
        if (boundPlan is not { Error: null, Plan: not null, Root: not null }
            || boundPlan.Current is null
            || !boundPlan.ReadyUnitAvailable
            || boundPlan.Next is null)
        {
            return false;
        }

        var nowMs = now.ToUnixTimeMilliseconds();
        boundPlan.Plan.PlanVersion++;
        boundPlan.Plan.UpdatedAt = nowMs;
        boundPlan.Current.Status = TaskNodeStatuses.Planned.ToString();
        boundPlan.Current.UpdatedAt = nowMs;

        events.Add(new(GoalEventTypes.CircuitOpened, GoalProducerComponents.Coordinator, new
        {
            kind = "replan",
            goalRunId = goal.GoalRunId,
            aggregateVersion = goal.AggregateVersion,
            iterationNumber = goal.IterationsStarted,
            planId = boundPlan.Plan.PlanId,
            planVersion = boundPlan.Plan.PlanVersion,
            demotedNodeId = boundPlan.Current.TaskNodeId,
            nextNodeId = boundPlan.Next.TaskNodeId,
            threshold = _noProgressBreakerThreshold,
            consecutiveNoProgress = goal.ConsecutiveNoProgress,
            consecutiveSameBlocker = goal.ConsecutiveSameBlocker,
            consecutiveInfraFailures = goal.ConsecutiveInfraFailures,
            progressFingerprint = goal.LastProgressFingerprint,
            blockerCode = decision.BlockerCode,
        }));
        return true;
    }

    private static void FailIncompleteBoundPlan(
        BoundPlanState? plan,
        string error,
        DateTimeOffset now)
    {
        if (plan?.Plan is null || plan.Root is null
            || plan.Plan.Status == TaskPlanStatuses.Completed.ToString())
            return;
        var nowMs = now.ToUnixTimeMilliseconds();
        plan.Plan.Status = TaskPlanStatuses.Failed.ToString();
        plan.Plan.ErrorMessage = error;
        plan.Plan.CompletedAt ??= nowMs;
        plan.Plan.UpdatedAt = nowMs;
        plan.Root.Status = TaskNodeStatuses.Failed.ToString();
        plan.Root.ErrorMessage = error;
        plan.Root.CompletedAt ??= nowMs;
        plan.Root.UpdatedAt = nowMs;
    }

    /// <summary>
    /// ADR-092 §6.2（G92-1 刀 B / P4）：结算拥有完成权——Goal 结算事务在宣告 Complete 的同一
    /// Serializable 事务内把绑定 Task 写成 canonical 终态（Completed + CompletedAtUtc，清空
    /// blocker），完成不再依赖外部通道。合法起点由 <see cref="TaskStateMachine.CanSettleCompleted"/>
    /// 显式声明，不修改通用迁移表；agent 侧 task_update 的完成旁路由 P5 守卫封死。
    /// </summary>
    /// <returns>true 表示确实写入了 Task 终态；状态不可结算时 fail-closed 跳过（不制造非法迁移）。</returns>
    private static bool CompleteBoundTask(
        PlatformDbContext db,
        TaskGoalBindingEntity binding,
        WorkspaceTaskEntity task,
        DateTimeOffset now,
        string goalRunId)
    {
        // 结算重放必须幂等：已经是 canonical Completed 时不得重写 CompletedAtUtc/清 blocker、
        // 不得再 +1 版本，也不得再落一条 TaskCompleted 事件——否则事件键 tgb-{taskId}-{version}
        // 会因再次 +1 而产生第二条，破坏"一次完成只释放一次、只有一个 canonical 事件"。
        if (task.Status == WorkspaceTaskStatus.Completed)
            return false;
        if (!TaskStateMachine.CanSettleCompleted(task.Status))
            return false;

        // 顺序即语义：终态 → 释放 assignment（attempt → task.ActiveAssignmentId）→ Version++ →
        // TaskCompleted 事件。事件键仍是 tgb-{taskId}-{version}：一次完成只 +1 版本、只落一条事件，
        // 因此键唯一性与结算幂等（gv-{goalId}-{epoch}-{iterationNo}）都不受影响。
        task.Status = WorkspaceTaskStatus.Completed;
        task.CompletedAtUtc = now;
        task.BlockerKind = null;
        task.BlockerReason = null;
        ReleaseAssignment(db, binding, task, now, AssignmentAttemptStatus.Completed);
        task.Version++;
        task.UpdatedAtUtc = now;
        AppendTaskEvent(db, task, binding, TaskEventType.TaskCompleted, now, goalRunId);
        return true;
    }

    private static bool ReleaseAssignment(
        PlatformDbContext db,
        TaskGoalBindingEntity binding,
        WorkspaceTaskEntity task,
        DateTimeOffset now,
        AssignmentAttemptStatus terminalStatus)
    {
        if (string.IsNullOrWhiteSpace(binding.AssignmentId))
            return false;

        var attempt = db.TaskAssignmentAttempts.Local.FirstOrDefault(
            item => item.AttemptId == binding.AssignmentId)
            ?? db.TaskAssignmentAttempts.SingleOrDefault(
                item => item.AttemptId == binding.AssignmentId);
        if (attempt is not null && attempt.ReleasedAtUtc is null)
        {
            attempt.Status = terminalStatus;
            attempt.ReleasedAtUtc = now;
            attempt.UpdatedAtUtc = now;
        }

        if (!string.Equals(task.ActiveAssignmentId, binding.AssignmentId, StringComparison.Ordinal))
            return false;
        task.ActiveAssignmentId = null;
        return true;
    }

    private static void ReleaseReservation(
        PlatformDbContext db,
        TaskGoalBindingEntity binding,
        DateTimeOffset now,
        string reason)
    {
        if (binding.ReservationId is null || binding.ReservationFencingToken is null)
            return;
        var reservation = db.AgentExecutionReservations.Local.FirstOrDefault(
            item => item.ReservationId == binding.ReservationId)
            ?? db.AgentExecutionReservations.SingleOrDefault(
                item => item.ReservationId == binding.ReservationId
                    && item.FencingToken == binding.ReservationFencingToken
                    && item.Status == "active");
        if (reservation is null)
            return;
        reservation.Status = "released";
        reservation.ReleaseReason = reason;
        reservation.ReleasedAtUtc = now;
        reservation.UpdatedAtUtc = now;
    }

    private void RenewReservation(
        PlatformDbContext db,
        TaskGoalBindingEntity binding,
        DateTimeOffset now)
    {
        if (binding.ReservationId is null || binding.ReservationFencingToken is null)
            return;
        var reservation = db.AgentExecutionReservations.Local.FirstOrDefault(
            item => item.ReservationId == binding.ReservationId)
            ?? db.AgentExecutionReservations.SingleOrDefault(
                item => item.ReservationId == binding.ReservationId
                    && item.FencingToken == binding.ReservationFencingToken
                    && item.Status == "active");
        if (reservation is null)
            return;
        reservation.LeaseUntilUtc = now.Add(_reservationLease);
        reservation.UpdatedAtUtc = now;
    }

    private static void AppendTaskEvent(
        PlatformDbContext db,
        WorkspaceTaskEntity task,
        TaskGoalBindingEntity binding,
        TaskEventType eventType,
        DateTimeOffset now,
        string goalRunId)
    {
        var persistedHead = db.TaskEvents
            .Where(item => item.TaskId == task.TaskId)
            .Max(item => (long?)item.Sequence) ?? 0;
        var localHead = db.TaskEvents.Local
            .Where(item => item.TaskId == task.TaskId)
            .Select(item => item.Sequence)
            .DefaultIfEmpty(0)
            .Max();
        var next = Math.Max(persistedHead, localHead) + 1;
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = $"tgb-{task.TaskId}-{task.Version}",
            TaskId = task.TaskId,
            WorkspaceId = task.WorkspaceId,
            Sequence = next,
            EventType = eventType,
            AssignmentId = binding.AssignmentId,
            AgentId = binding.AgentInstanceId,
            SessionId = binding.GoalRunId,
            CorrelationId = goalRunId,
            CausationId = binding.BindingId,
            CreatedAtUtc = now,
        });
    }

    private static async Task AppendGoalEventsAsync(
        PlatformDbContext db,
        GoalRunEntity goal,
        GoalIterationEntity iteration,
        IReadOnlyList<GoalEventDraft> drafts,
        CancellationToken ct)
    {
        var head = await db.ConversationHeads.SingleOrDefaultAsync(
            item => item.ConversationId == goal.CurrentConversationId, ct);
        var previous = head?.HeadSequence ?? 0;
        if (head is null)
        {
            head = new ConversationHeadEntity { ConversationId = goal.CurrentConversationId };
            db.ConversationHeads.Add(head);
        }
        head.HeadSequence = previous + drafts.Count;

        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = previous + index + 1,
                EventId = $"gs{index}-{iteration.GoalIterationId}-{goal.AggregateVersion}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = iteration.TurnId ?? string.Empty,
                CommandId = iteration.CommandId,
                RunId = iteration.RunId,
                Type = draft.EventType,
                SchemaVersion = 1,
                Payload = JsonSerializer.Serialize(draft.Payload, JsonOpts),
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = goal.GoalRunId,
                CausationId = iteration.TurnId,
                AgentId = goal.AgentInstanceId,
                SourceKind = "goal",
                TraceId = iteration.TraceId,
                ProducerComponent = draft.ProducerComponent,
            });
        }
    }

    private static object VerdictPayload(
        GoalRunEntity goal,
        GoalIterationEntity iteration,
        GoalVerificationDecision decision) => new
    {
        goalRunId = goal.GoalRunId,
        activationEpoch = goal.ActivationEpoch,
        aggregateVersion = goal.AggregateVersion,
        iterationNumber = iteration.IterationNo,
        verdict = ToWire(decision.Verdict),
        reason = decision.Reason,
        blockerCode = decision.BlockerCode,
    };

    private static GoalVerificationDecision BlockedDecision(
        string code,
        string message,
        IReadOnlyList<string> refs) => new()
    {
        Verdict = GoalVerificationVerdict.Blocked,
        Reason = message,
        EvidenceRefs = refs,
        BlockerCode = code,
        BlockerMessage = message,
        NextAction = "Resolve the blocker, then explicitly resume the Goal.",
    };

    private static string ToWire(GoalVerificationVerdict verdict) => verdict switch
    {
        GoalVerificationVerdict.Continue => "continue",
        GoalVerificationVerdict.Complete => "complete",
        GoalVerificationVerdict.Blocked => "blocked",
        GoalVerificationVerdict.NeedsUser => "needs_user",
        GoalVerificationVerdict.Unsafe => "unsafe",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private sealed record GoalEventDraft(string EventType, string ProducerComponent, object Payload);

    /// <summary>
    /// 绑定执行计划的结算视图。ADR-092 §13.1（G92-1 刀 C）：<see cref="Current"/> 允许为 null——
    /// "0 个 Running 且必需单元全部已验证完成"（合法收敛态）与"0 个 Running 但仍有必需单元"都是合法状态，
    /// 调用方必须接受无 Current，而不是当成结构错误或空指针。
    /// <see cref="RemainingRequiredUnits"/> = 本次裁决后仍未验证完成的必需单元数（null = 未知，不得冒充 0）；
    /// 只有它为 0 时才允许 goal 作用域的完成。<see cref="ReadyUnitAvailable"/> 用于区分
    /// "0 Running 但可认领"（认领推进）与"等依赖"（typed wait）。
    /// </summary>
    private sealed record BoundPlanState(
        TaskPlanRunEntity? Plan,
        TaskNodeEntity? Root,
        TaskNodeEntity? Current,
        TaskNodeEntity? Next,
        string? Error,
        int? RemainingRequiredUnits = null,
        int RunningUnitCount = 0,
        bool ReadyUnitAvailable = false)
    {
        public static BoundPlanState Invalid(string error) => new(null, null, null, null, error);
    }
}
