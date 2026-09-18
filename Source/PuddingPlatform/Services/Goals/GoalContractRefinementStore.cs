using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>A1 合同整理的提交结局（规格 §A1_SHAPE 6/7/8）。</summary>
public enum GoalContractRefinementStatus
{
    /// <summary>CAS 成功；审计事件已与合同更新同事务提交。</summary>
    Applied = 0,

    /// <summary>同 operation key 已应用过（幂等重放；绝不二次覆盖）。</summary>
    AlreadyApplied = 1,

    /// <summary>expectedContractVersion 与当前行不符（source 仍为 bounded_planning 的陈旧提议）。</summary>
    StaleContract = 2,

    /// <summary>合同已被其他整理占用（source 非 bounded_planning 且 op key 不匹配）⇒ 转用户裁决。</summary>
    NeedsUser = 3,

    /// <summary>proposal 内容/目标状态 fail-closed 拒绝（未触碰合同）。</summary>
    Rejected = 4,
}

/// <summary>提交结果；Applied 时 ContractVersion 为新版本，其余为当前行版本（可得时）。</summary>
public sealed record GoalContractRefinementResult
{
    public required GoalContractRefinementStatus Status { get; init; }

    public string? Reason { get; init; }

    public int? ContractVersion { get; init; }

    public string? OperationKey { get; init; }

    public static GoalContractRefinementResult Of(
        GoalContractRefinementStatus status,
        string? reason = null,
        int? contractVersion = null,
        string? operationKey = null) => new()
    {
        Status = status,
        Reason = reason,
        ContractVersion = contractVersion,
        OperationKey = operationKey,
    };
}

/// <summary>
/// G92-1 S1-c 片6（A1）3a：合同整理 CAS 提交存储（规格 §A1_SHAPE 5/6/7/8）。
/// <para>
/// 单一 SQLite 事务内完成：goal 态复验（存在且 Active，objective/epoch/objectiveVersion 与
/// candidate 逐项相等）→ 幂等探针（同 operation key ⇒ already-applied，先于 source/version 判定）
/// → source/版本预检 → CAS UPDATE（WHERE contract_version=expected AND source='bounded_planning'，
/// 0 行即冲突，重读后分流）→ 成功同事务追加 goal.contract_refined 审计事件（payload 只存
/// operation key / 新旧版本 / criterion IDs / hash 摘要，绝无自然语言）。
/// 一次性闸：成功后 source=agent_refined；同 (goal, epoch, objectiveVersion) 再次提议只能
/// already-applied / needs_user，绝不二次覆盖。不新增表/列，不引入 DB migration。
/// </para>
/// </summary>
public sealed class GoalContractRefinementStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>审计事件 EventId 前缀 + operation key hex 前 16 位（≤64 字符约束，确定性幂等锚点）。</summary>
    private const string AuditEventIdPrefix = "gcr-";

    /// <summary>Worker 的唯一入口：验证（纯函数）+ CAS 提交（单事务）一步完成。</summary>
    public async Task<GoalContractRefinementResult> TryApplyAsync(
        GoalContractProposalFacts facts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var validation = GoalContractProposalValidator.Validate(facts);
        if (!validation.IsAccepted || validation.Plan is null)
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.Rejected,
                validation.RejectionReason ?? "proposal rejected");

        var plan = validation.Plan;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 1) Goal 态复验：身份以 DB 为准，candidate 事实逐项对回（防伪：不信 proposal 自报身份）。
        var goal = await db.GoalRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GoalRunId == facts.GoalRunId, ct);
        if (goal is null)
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.Rejected, $"goal run not found: {facts.GoalRunId}");

        if (goal.Status != GoalPhase.Active)
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.Rejected, $"goal must be active, got {goal.Status}");

        if (goal.ActivationEpoch != facts.ActivationEpoch
            || goal.ObjectiveVersion != facts.ObjectiveVersion
            || !string.Equals(goal.Objective, facts.Objective, StringComparison.Ordinal))
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.Rejected,
                "candidate identity does not match the persisted goal (epoch/objectiveVersion/objective)");

        // 2) 合同行预读（后续 CAS 以 WHERE 兜底并发）。
        var contractId = GoalVerificationPersistence.BuildContractId(
            facts.GoalRunId, facts.ActivationEpoch, facts.ObjectiveVersion);
        var contract = await db.GoalAcceptanceContracts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ContractId == contractId, ct);
        if (contract is null)
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.Rejected,
                $"acceptance contract missing: {contractId}");

        // 3) 幂等探针先于 source/version 判定：同 operation key 的重放必须得到 already-applied，
        //    即使合同 source 已是 agent_refined、版本已递增（规格 §6 CAS 冲突后的重读语义）。
        if (await FindAppliedOperationKeyAsync(db, facts.GoalRunId, plan.OperationKey, ct))
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.AlreadyApplied,
                "same operation key already applied",
                contract.ContractVersion,
                plan.OperationKey);

        // 4) 一次性闸：合同已非 bounded_planning 且 op key 不匹配 ⇒ 其他整理已占用，转用户裁决。
        if (!string.Equals(contract.Source, GoalAcceptanceContractSources.BoundedPlanning, StringComparison.Ordinal))
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.NeedsUser,
                $"contract source is '{contract.Source}', not "
                + $"'{GoalAcceptanceContractSources.BoundedPlanning}'; contract_refinement_conflict needs user adjudication",
                contract.ContractVersion,
                plan.OperationKey);

        // 5) 版本防伪：陈旧期望值 ⇒ stale（不自动合并，保留 objective 与既有合同）。
        if (contract.ContractVersion != plan.ExpectedContractVersion)
            return GoalContractRefinementResult.Of(
                GoalContractRefinementStatus.StaleContract,
                $"expectedContractVersion {plan.ExpectedContractVersion} != current {contract.ContractVersion}",
                contract.ContractVersion,
                plan.OperationKey);

        // 6) CAS 提交：只动 内容/来源/版本/时间戳；PlanFingerprint 保留原值（计划绑定事实不因整理改变）。
        var newVersion = plan.ExpectedContractVersion + 1;
        var now = DateTimeOffset.UtcNow;
        var rows = await db.GoalAcceptanceContracts
            .Where(item => item.ContractId == contractId
                && item.ContractVersion == plan.ExpectedContractVersion
                && item.Source == GoalAcceptanceContractSources.BoundedPlanning)
            .ExecuteUpdateAsync(set => set
                .SetProperty(item => item.ContractVersion, newVersion)
                .SetProperty(item => item.CriteriaJson, GoalVerificationPersistence.SerializeCriteria(plan.Criteria))
                .SetProperty(item => item.ChecksJson, GoalVerificationPersistence.SerializeChecks(plan.Checks))
                .SetProperty(item => item.Source, GoalAcceptanceContractSources.AgentRefined)
                .SetProperty(item => item.UpdatedAtUtc, now), ct);

        if (rows == 0)
        {
            // 7) CAS 冲突：重读后分流（同 op key ⇒ already-applied；否则 stale / conflict）。
            var conflicted = await db.GoalAcceptanceContracts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ContractId == contractId, ct);
            if (conflicted is not null
                && await FindAppliedOperationKeyAsync(db, facts.GoalRunId, plan.OperationKey, ct))
                return GoalContractRefinementResult.Of(
                    GoalContractRefinementStatus.AlreadyApplied,
                    "same operation key already applied",
                    conflicted.ContractVersion,
                    plan.OperationKey);

            return conflicted is null
                ? GoalContractRefinementResult.Of(
                    GoalContractRefinementStatus.Rejected, "acceptance contract vanished during CAS", null, plan.OperationKey)
                : string.Equals(conflicted.Source, GoalAcceptanceContractSources.BoundedPlanning, StringComparison.Ordinal)
                    ? GoalContractRefinementResult.Of(
                        GoalContractRefinementStatus.StaleContract,
                        $"cas conflict: expected {plan.ExpectedContractVersion}, current {conflicted.ContractVersion}",
                        conflicted.ContractVersion,
                        plan.OperationKey)
                    : GoalContractRefinementResult.Of(
                        GoalContractRefinementStatus.NeedsUser,
                        $"cas conflict: contract source is '{conflicted.Source}'",
                        conflicted.ContractVersion,
                        plan.OperationKey);
        }

        // 8) 成功：同事务追加审计事件 + 推进会话事件头，一次性提交（合同 UPDATE 已即时生效）。
        await AppendRefinementAuditEventAsync(db, goal, plan, contract.ContractVersion, newVersion, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return GoalContractRefinementResult.Of(
            GoalContractRefinementStatus.Applied,
            "contract refined",
            newVersion,
            plan.OperationKey);
    }

    /// <summary>
    /// 在 goal.contract_refined 审计事件中查找同 operation key（CorrelationId=goalRunId 圈定范围，
    /// payload JSON fail-safe 解析，只认精确相等）。
    /// </summary>
    private static async Task<bool> FindAppliedOperationKeyAsync(
        PlatformDbContext db,
        string goalRunId,
        string operationKey,
        CancellationToken ct)
    {
        var payloads = await db.ConversationEvents.AsNoTracking()
            .Where(item => item.Type == GoalEventTypes.ContractRefined
                && item.CorrelationId == goalRunId)
            .Select(item => item.Payload)
            .ToListAsync(ct);

        foreach (var payload in payloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("operationKey", out var element)
                    && string.Equals(element.GetString(), operationKey, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // fail-safe：无法解析的审计事件不参与幂等判定（fail-closed：宁可重放不误判已应用）。
            }
        }

        return false;
    }

    /// <summary>
    /// 审计事件（GoalRunStore/GoalSettlementStore 同款 envelope）：SourceKind=goal、
    /// CorrelationId=goalRunId、ProducerComponent=coordinator；payload 只存 operation key、
    /// old/new ContractVersion、criterion IDs、hash 摘要 —— 绝不存原始自然语言。
    /// </summary>
    private static async Task AppendRefinementAuditEventAsync(
        PlatformDbContext db,
        GoalRunEntity goal,
        GoalContractRefinementPlan plan,
        int oldContractVersion,
        int newContractVersion,
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
        head.HeadSequence = previous + 1;

        var operationKeyHex = plan.OperationKey.StartsWith("sha256:", StringComparison.Ordinal)
            ? plan.OperationKey["sha256:".Length..]
            : plan.OperationKey;

        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = goal.CurrentConversationId,
            Sequence = previous + 1,
            EventId = $"{AuditEventIdPrefix}{operationKeyHex[..16]}",
            WorkspaceId = goal.WorkspaceId,
            TurnId = plan.TurnId,
            CommandId = null,
            RunId = null,
            MessageId = null,
            Type = GoalEventTypes.ContractRefined,
            SchemaVersion = 1,
            Payload = JsonSerializer.Serialize(new
            {
                operationKey = plan.OperationKey,
                oldContractVersion,
                newContractVersion,
                criterionIds = plan.CriterionIds,
                definitionHashes = plan.DefinitionHashes,
            }, JsonOpts),
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = goal.GoalRunId,
            CausationId = plan.TurnId,
            ProducerEventId = null,
            AgentId = goal.AgentInstanceId,
            SourceKind = "goal",
            TraceId = null,
            ProducerComponent = GoalProducerComponents.Coordinator,
        });
    }
}
