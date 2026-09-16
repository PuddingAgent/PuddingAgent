using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Goals;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// 「Agent 自主恢复 Goal」的平台层实现。只复用 canonical resume 路径
/// （<see cref="IGoalCommandService"/>.ExecuteAsync → GoalCommandService.HandleResumeAsync
/// ：Active 幂等 / GoalStateMachine.CanResume 裁决 / ExpectedVersion CAS / TryMutateAsync
/// 变异 + goal.resumed 事件 + continuation 入队），不复制状态机判断、不直写 goal_runs、
/// 不新增第二条恢复路径。
/// <para>
/// 本实现新增的三道闸门全部是<b>只读前置检查</b>，位于委托之前，失败路径零写入：
/// ① 归属校验 —— FindActiveAsync 本身按 (conversation, agent) 过滤；按 GoalRunId /
///    FindLatestAsync 定位到的他人 Goal 在此拦截（goal.held_by_other_agent）。
/// ② 熔断证据闸门 —— blocked_code=no_progress_circuit_open（GoalSettlementStore.cs:1387
///    的 private 常量，字面量对齐见 <see cref="GoalResumeCodes.CircuitBlockerCode"/>）时
///    EvidenceRefs 必填非空（goal.circuit_evidence_required）。
/// ③ epoch 自动恢复配额 —— 同一 activation epoch 内自动恢复上限 1 次
///    （goal.resume_epoch_limit），实现方案见 <see cref="_autoResumedEpochs"/> 注释。
/// </para>
/// <para>
/// GoalCommandKind.Resume 无「仅用户 slash / HTTP 入口」注释（对照 Extend 的
/// GoalContracts.cs:47-53），结构化复用合法；UserId 以发起 Agent 身份填充
/// （resume 路径不产生用户归属语义，GoalCommandService 的 resume 分支不消费 UserId）。
/// </para>
/// </summary>
public sealed class GoalResumeService(
    GoalRunStore store,
    IGoalCommandService goalCommands,
    ILogger<GoalResumeService> logger) : IGoalResumeService
{
    /// <summary>
    /// 进程内自动恢复台账：goalRunId → 上一次自动 resume 完成后的 ActivationEpoch。
    /// 判定规则：台账值 == goal 当前 ActivationEpoch ⇒ 本次激活纪元内已自动恢复过 ⇒ 拒绝；
    /// epoch 由一切状态转换递增（GoalCommandService.cs:180/220/262/323/433/546、
    /// GoalSettlementStore.cs:1118/1223/1313、GoalRestartReconciler.cs:65/95、
    /// TaskGoalDispatchTransactionStore.cs:393），任何后续 pause/blocked/人工/重启事件
    /// 都会推进 epoch 并重置配额。blocked 循环的频控由熔断证据闸门（闸门②）承担。
    /// 已知边界：台账为进程内状态，宿主重启后清零 —— 持久化（事件载荷或专用表）涉及
    /// 禁改文件，留待后续波次；重启场景由 GoalRestartReconciler 的 boot fence 语义兜底。
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _autoResumedEpochs = new();

    /// <inheritdoc />
    public async Task<GoalResumeResult> ResumeAsync(
        GoalResumeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentInstanceId);

        // ── 定位（只读）─────────────────────────────────────────────────────
        GoalRunEntity? goal;
        if (!string.IsNullOrWhiteSpace(request.GoalRunId))
        {
            goal = await store.FindAsync(request.GoalRunId, ct);
            if (goal is null || goal.CurrentConversationId != request.ConversationId)
            {
                // 不区分「不存在」与「不属于本会话」：不向工具调用方泄露跨会话 goal 的存在性。
                return Refuse(GoalResumeCodes.NotFound,
                    "目标 Goal 不存在或不属于当前会话。", null);
            }
        }
        else
        {
            goal = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
            if (goal is null)
            {
                // 终态 Goal（含 budget_exhausted）不在 FindActive 集合内；
                // 回退会话最新 Goal，让终态在下方被 goal.not_resumable 精确拒绝。
                goal = await store.FindLatestAsync(request.WorkspaceId, request.ConversationId, ct);
                if (goal is null)
                {
                    return Refuse(GoalResumeCodes.NotFound, "当前会话没有 Goal。", null);
                }
            }
        }

        // ── 闸门①：归属校验（fail-closed，两条定位路径共用）──────────────────
        if (!string.Equals(goal.AgentInstanceId, request.AgentInstanceId, StringComparison.Ordinal))
        {
            return Refuse(GoalResumeCodes.HeldByOtherAgent,
                $"Goal 归属其他 Agent（owner={goal.AgentInstanceId}），不能由当前 Agent 恢复。",
                goal.ToSnapshot());
        }

        // ── 幂等：已 active（Success=true / Resumed=false，零写入）────────────
        if (goal.Status == GoalPhase.Active)
        {
            return SnapshotResult(
                success: true, resumed: false, code: GoalResumeCodes.AlreadyActive,
                message: "Goal 已经处于 active。",
                snapshot: goal.ToSnapshot());
        }

        // ── 闸门②：熔断证据（fail-closed）──────────────────────────────────
        var circuitBlocked = goal.Status == GoalPhase.Blocked
            && string.Equals(
                goal.BlockedCode,
                GoalResumeCodes.CircuitBlockerCode,
                StringComparison.Ordinal);
        if (circuitBlocked
            && request.EvidenceRefs is null or { Count: 0 })
        {
            return Refuse(GoalResumeCodes.CircuitEvidenceRequired,
                "Goal 因无进展熔断而阻塞（no_progress_circuit_open）；自动恢复必须提供"
                + "非空 EvidenceRefs（人工介入或阻塞解除的证据引用）。",
                goal.ToSnapshot());
        }

        // ── 闸门③：同一 activation epoch 内自动恢复上限 1 次 ─────────────────
        if (_autoResumedEpochs.TryGetValue(goal.GoalRunId, out var resumedEpoch)
            && resumedEpoch == goal.ActivationEpoch)
        {
            return Refuse(GoalResumeCodes.ResumeEpochLimit,
                $"同一激活纪元（activation_epoch={goal.ActivationEpoch}）内自动恢复上限 1 次；"
                + "需人工介入或等待新的状态转换推进 epoch 后重试。",
                goal.ToSnapshot());
        }

        // ── 终态精确拒绝（复用 GoalStateMachine.CanResume，不复制判断；仅用于把
        //    拒绝码精确化为 goal.not_resumable —— HandleResumeAsync 内部会基于同一
        //    查询再裁决一次，变异由 TryMutateAsync 的 CAS 保护）────────────────
        if (!GoalStateMachine.CanResume(goal.Status))
        {
            var message = $"Goal 已处于终态 {goal.Status}，不能 resume。";
            if (goal.Status == GoalPhase.BudgetExhausted)
            {
                message += "budget_exhausted 不可恢复：需要新预算请使用 /goal extend（人工权能）或显式新建 Goal。";
            }

            return Refuse(GoalResumeCodes.NotResumable, message, goal.ToSnapshot());
        }

        // ── 委托 canonical resume ───────────────────────────────────────────
        // 确定性幂等锚点：同一 epoch 的第二次自动恢复已被闸门③拦截，因此该键在
        // (agent, goal, epoch) 维度天然唯一；重试同一请求产生相同键，续行链可安全去重。
        var clientRequestId =
            $"goal-resume:{request.AgentInstanceId}:{goal.GoalRunId}:e{goal.ActivationEpoch}";
        var commandResult = await goalCommands.ExecuteAsync(
            new GoalCommandRequest(
                request.WorkspaceId,
                request.ConversationId,
                request.AgentInstanceId,
                request.AgentInstanceId,
                clientRequestId,
                new GoalCommand { Kind = GoalCommandKind.Resume, Reason = request.Reason },
                SourceChannel: "agent",
                ExpectedVersion: request.ExpectedVersion),
            ct);

        if (!commandResult.Success)
        {
            // canonical 拒绝码 → 本端口稳定 wire 码映射；未知码（如 goal_disabled）原样透传。
            var code = commandResult.ErrorCode switch
            {
                GoalErrorCodes.GoalNotFound => GoalResumeCodes.NotFound,
                GoalErrorCodes.InvalidState => GoalResumeCodes.NotResumable,
                GoalErrorCodes.VersionConflict => GoalResumeCodes.VersionConflict,
                var other => other ?? GoalResumeCodes.NotResumable,
            };
            logger.LogWarning(
                "[GoalResume] delegated resume refused goal={GoalRunId} agent={AgentInstanceId} "
                + "conv={ConversationId} upstreamCode={UpstreamCode} mappedCode={Code}",
                goal.GoalRunId, request.AgentInstanceId, request.ConversationId,
                commandResult.ErrorCode, code);
            return SnapshotResult(
                success: false, resumed: false, code: code,
                message: commandResult.Message,
                snapshot: commandResult.Snapshot);
        }

        // 成功：记录本次激活纪元（resume 后 epoch 已 +1），供闸门③裁决后续请求。
        _autoResumedEpochs[goal.GoalRunId] = commandResult.Snapshot!.ActivationEpoch;
        logger.LogInformation(
            "[GoalResume] resumed goal={GoalRunId} agent={AgentInstanceId} epoch={Epoch} "
            + "iteration={Started}/{Max} evidence={EvidenceCount}",
            goal.GoalRunId, request.AgentInstanceId, commandResult.Snapshot.ActivationEpoch,
            commandResult.Snapshot.IterationsStarted, commandResult.Snapshot.MaxIterations,
            request.EvidenceRefs?.Count ?? 0);

        return SnapshotResult(
            success: true, resumed: true, code: GoalResumeCodes.Resumed,
            message: commandResult.Message,
            snapshot: commandResult.Snapshot);
    }

    private static GoalResumeResult Refuse(string code, string message, GoalSnapshot? snapshot)
        => SnapshotResult(success: false, resumed: false, code: code, message: message, snapshot: snapshot);

    private static GoalResumeResult SnapshotResult(
        bool success,
        bool resumed,
        string code,
        string? message,
        GoalSnapshot? snapshot)
        => new()
        {
            Success = success,
            Resumed = resumed,
            Code = code,
            Message = message,
            GoalRunId = snapshot?.GoalRunId,
            Phase = snapshot?.Phase,
            BlockedCode = snapshot?.BlockedCode,
            ActivationEpoch = snapshot?.ActivationEpoch,
            AggregateVersion = snapshot?.AggregateVersion,
            IterationsStarted = snapshot?.IterationsStarted,
            IterationsSettled = snapshot?.IterationsSettled,
            MaxIterations = snapshot?.MaxIterations,
        };
}
