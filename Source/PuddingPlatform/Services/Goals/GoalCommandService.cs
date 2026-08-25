using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-074 §4: Goal 命令应用服务 —— slash 文本入口与结构化 Control Plane API 共用。
/// G1 语义：命令不创建任何 Agent Turn / ChatExecutionCommand；状态可重启重放；
/// 同一会话同时最多一个非终态 Goal；resume 不重置已消费轮数。
/// </summary>
public sealed class GoalCommandService(
    GoalRunStore store,
    IOptions<GoalRunOptions> options,
    TimeProvider timeProvider,
    ILogger<GoalCommandService> logger) : IGoalCommandService
{
    public async Task<GoalCommandResult> ExecuteAsync(
        GoalCommandRequest request,
        CancellationToken ct = default)
    {
        // G6 回滚语义：feature flag 停止新 Goal 与（未来的）continuation，
        // 但保留读取、pause/cancel 和既有事实。
        var readOnlyOrContainment = request.Command.Kind
            is GoalCommandKind.Status
            or GoalCommandKind.Pause
            or GoalCommandKind.Cancel
            or GoalCommandKind.Clear;
        if (!options.Value.Enabled && !readOnlyOrContainment)
        {
            return GoalCommandResult.Fail(
                GoalErrorCodes.GoalDisabled,
                "Goal 功能当前未启用（GoalRuns:Enabled=false）。只读 status 与 pause/cancel 仍可用；" +
                "创建、编辑或恢复 Goal 需要在 system.json 的 GoalRuns 节打开开关。");
        }

        try
        {
            return request.Command.Kind switch
            {
                GoalCommandKind.Status => await HandleStatusAsync(request, ct),
                GoalCommandKind.Set => await HandleSetAsync(request, ct),
                GoalCommandKind.Edit => await HandleEditAsync(request, ct),
                GoalCommandKind.Replace => await HandleReplaceAsync(request, ct),
                GoalCommandKind.Pause => await HandlePauseAsync(request, ct),
                GoalCommandKind.Resume => await HandleResumeAsync(request, ct),
                GoalCommandKind.Cancel => await HandleCancelAsync(request, ct),
                GoalCommandKind.Clear => await HandleClearAsync(request, ct),
                _ => GoalCommandResult.Fail(
                    GoalErrorCodes.InvalidCommand,
                    $"未知的 Goal 命令 '{request.Command.Kind}'。"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[GoalCommand] failed kind={Kind} conv={ConversationId} workspace={WorkspaceId} user={UserId}",
                request.Command.Kind, request.ConversationId, request.WorkspaceId, request.UserId);
            return GoalCommandResult.Fail(
                "goal_internal_error",
                "Goal 命令执行失败，请查看后端诊断日志。");
        }
    }

    private async Task<GoalCommandResult> HandleStatusAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var goal = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
        if (goal is null)
        {
            var latest = await store.FindLatestAsync(request.WorkspaceId, request.ConversationId, ct);
            if (latest is null)
            {
                return GoalCommandResult.Ok(
                    "当前会话没有 Goal。使用 `/goal <objective> [--rounds N]` 创建（N ∈ 1..256，默认 256）。",
                    EmptySnapshot(request));
            }

            return GoalCommandResult.Ok(BuildStatusMessage(latest), latest.ToSnapshot());
        }

        return GoalCommandResult.Ok(BuildStatusMessage(goal), goal.ToSnapshot());
    }

    private async Task<GoalCommandResult> HandleSetAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var objective = request.Command.Objective!;
        if (!TryResolveRounds(request, out var rounds, out var roundsError))
            return roundsError;

        // 幂等重放：同一 clientRequestId 重投返回首次结果，不创建第二个 Goal。
        var replayed = await store.FindBySourceCommandAsync(request.ClientRequestId, ct);
        if (replayed is not null)
        {
            logger.LogInformation(
                "[GoalCommand] Idempotent replay goal={GoalRunId} command={CommandId}",
                replayed.GoalRunId, request.ClientRequestId);
            return GoalCommandResult.Ok(BuildStatusMessage(replayed), replayed.ToSnapshot());
        }

        var active = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
        if (active is not null)
        {
            return GoalCommandResult.Fail(
                GoalErrorCodes.GoalConflict,
                "当前会话已有一个非终态 Goal；改变目标必须显式 /goal edit 或 /goal replace。",
                active.ToSnapshot());
        }

        var now = DateTimeOffset.UtcNow;
        var goal = new GoalRunEntity
        {
            GoalRunId = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            CurrentConversationId = request.ConversationId,
            AgentInstanceId = request.AgentInstanceId,
            Objective = objective,
            ObjectiveVersion = 1,
            Status = GoalPhase.Active,
            MaxIterations = rounds,
            IterationsStarted = 0,
            IterationsSettled = 0,
            ActivationEpoch = 1,
            AggregateVersion = 1,
            CreatedByUserId = request.UserId,
            SourceChannel = request.SourceChannel ?? "web",
            SourceCommandId = request.ClientRequestId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await store.CreateAsync(goal, request.ClientRequestId, ct);

        logger.LogInformation(
            "[GoalCommand] Created goal={GoalRunId} conv={ConversationId} rounds={Rounds} channel={Channel} user={UserId}",
            goal.GoalRunId, request.ConversationId, rounds, goal.SourceChannel, request.UserId);

        return GoalCommandResult.Ok(
            $"Goal 已创建并处于 active。Iteration 0/{rounds}。使用 /goal 查看状态，/goal pause 暂停。",
            goal.ToSnapshot());
    }

    private async Task<GoalCommandResult> HandleEditAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var goal = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
        if (goal is null)
            return GoalCommandResult.Fail(GoalErrorCodes.GoalNotFound, "当前会话没有可编辑的 Goal。");

        if (request.ExpectedVersion is > 0 && request.ExpectedVersion != goal.AggregateVersion)
            return VersionConflict(goal);

        if (!GoalStateMachine.CanEdit(goal.Status))
            return GoalCommandResult.Fail(
                GoalErrorCodes.InvalidState,
                $"终态 Goal（{goal.Status}）不能编辑；请新建 Goal。",
                goal.ToSnapshot());

        var (mutated, _) = await store.TryMutateAsync(
            goal.GoalRunId,
            request.ExpectedVersion ?? 0,
            g =>
            {
                g.Objective = request.Command.Objective!;
                g.ObjectiveVersion++;
                // ADR-074 §5.2：edit 递增 activation epoch，使旧续行意图失效。
                g.ActivationEpoch++;
                return true;
            },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Edited, new { reason = "user_edit" }),
            request.ClientRequestId,
            ct);

        return mutated is null
            ? VersionConflict(goal)
            : GoalCommandResult.Ok(
                $"Goal objective 已更新（revision {mutated.ObjectiveVersion}）；已消费 Iteration {mutated.IterationsStarted}/{mutated.MaxIterations} 保持不变。",
                mutated.ToSnapshot());
    }

    private async Task<GoalCommandResult> HandleReplaceAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var active = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
        if (active is null)
            return await HandleSetAsync(request, ct);

        if (request.ExpectedVersion is > 0 && request.ExpectedVersion != active.AggregateVersion)
            return VersionConflict(active);

        if (!GoalStateMachine.CanTransition(active.Status, GoalPhase.Cancelled))
            return GoalCommandResult.Fail(
                GoalErrorCodes.InvalidState,
                $"Goal 处于 {active.Status}，无法 replace。",
                active.ToSnapshot());

        // 两步提交：先终态旧 Goal，再创建新 Goal。两步之间崩溃时旧 Goal 已终态、
        // 新 Goal 未建 —— 客户端重试同一 clientRequestId 幂等补建（source_command_id 唯一）。
        var (cancelled, _) = await store.TryMutateAsync(
            active.GoalRunId,
            request.ExpectedVersion ?? 0,
            g =>
            {
                g.Status = GoalPhase.Cancelled;
                g.StatusReason = request.Command.Reason ?? "replaced";
                g.ActivationEpoch++;
                g.TerminalAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                return true;
            },
            new GoalRunStore.GoalEventAppend(
                GoalEventTypes.Cancelled,
                new { reason = request.Command.Reason ?? "replaced" }),
            request.ClientRequestId,
            ct);

        if (cancelled is null)
            return VersionConflict(active);

        return await HandleSetAsync(request, ct);
    }

    private async Task<GoalCommandResult> HandlePauseAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var goal = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
        if (goal is null)
            return GoalCommandResult.Fail(GoalErrorCodes.GoalNotFound, "当前会话没有 Goal。");

        if (goal.Status == GoalPhase.Paused)
            return GoalCommandResult.Ok("Goal 已经处于 paused。", goal.ToSnapshot());

        if (request.ExpectedVersion is > 0 && request.ExpectedVersion != goal.AggregateVersion)
            return VersionConflict(goal);

        if (!GoalStateMachine.CanTransition(goal.Status, GoalPhase.Paused))
            return GoalCommandResult.Fail(
                GoalErrorCodes.InvalidState,
                $"Goal 处于 {goal.Status}，不能 pause。",
                goal.ToSnapshot());

        var (mutated, _) = await store.TryMutateAsync(
            goal.GoalRunId,
            request.ExpectedVersion ?? 0,
            g =>
            {
                g.Status = GoalPhase.Paused;
                g.StatusReason = request.Command.Reason;
                g.ActivationEpoch++;
                return true;
            },
            new GoalRunStore.GoalEventAppend(
                GoalEventTypes.Paused,
                new { reason = request.Command.Reason }),
            request.ClientRequestId,
            ct);

        return mutated is null
            ? VersionConflict(goal)
            : GoalCommandResult.Ok(
                $"Goal 已暂停（已消费 Iteration {mutated.IterationsStarted}/{mutated.MaxIterations} 保留）。/goal resume 恢复。",
                mutated.ToSnapshot());
    }

    private async Task<GoalCommandResult> HandleResumeAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var goal = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);

        // 终态 Goal（含 budget_exhausted）不在 FindActive 集合内；
        // resume 的拒绝必须给出准确原因，而不是误导性的"没有 Goal"。
        if (goal is null)
        {
            var latest = await store.FindLatestAsync(request.WorkspaceId, request.ConversationId, ct);
            if (latest is not null && GoalStateMachine.IsTerminal(latest.Status))
            {
                return GoalCommandResult.Fail(
                    GoalErrorCodes.InvalidState,
                    latest.Status == GoalPhase.BudgetExhausted
                        ? "Goal 已达到 accepted Iteration 硬上限，resume 不会重置额度；请新建 Goal。"
                        : $"Goal 已处于终态 {latest.Status}，不能 resume；请新建 Goal。",
                    latest.ToSnapshot());
            }

            return GoalCommandResult.Fail(GoalErrorCodes.GoalNotFound, "当前会话没有 Goal。");
        }

        if (goal.Status == GoalPhase.Active)
            return GoalCommandResult.Ok("Goal 已经处于 active。", goal.ToSnapshot());

        // ADR-074 §5.1：budget_exhausted 不可 resume —— 需要新预算必须显式新建 Goal。
        if (!GoalStateMachine.CanResume(goal.Status))
            return GoalCommandResult.Fail(
                GoalErrorCodes.InvalidState,
                $"终态 Goal（{goal.Status}）不能 resume。",
                goal.ToSnapshot());

        if (request.ExpectedVersion is > 0 && request.ExpectedVersion != goal.AggregateVersion)
            return VersionConflict(goal);

        var (mutated, _) = await store.TryMutateAsync(
            goal.GoalRunId,
            request.ExpectedVersion ?? 0,
            g =>
            {
                g.Status = GoalPhase.Active;
                g.StatusReason = null;
                g.BlockedCode = null;
                g.BlockedMessage = null;
                g.ActivationEpoch++;
                return true;
            },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Resumed, new { }),
            request.ClientRequestId,
            ct);

        return mutated is null
            ? VersionConflict(goal)
            : GoalCommandResult.Ok(
                $"Goal 已恢复 active（Iteration {mutated.IterationsStarted}/{mutated.MaxIterations}，resume 不重置已消费额度）。",
                mutated.ToSnapshot());
    }

    private async Task<GoalCommandResult> HandleCancelAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var goal = await store.FindActiveAsync(request.ConversationId, request.AgentInstanceId, ct);
        if (goal is null)
            return GoalCommandResult.Fail(GoalErrorCodes.GoalNotFound, "当前会话没有 Goal。");

        if (request.ExpectedVersion is > 0 && request.ExpectedVersion != goal.AggregateVersion)
            return VersionConflict(goal);

        if (GoalStateMachine.IsTerminal(goal.Status))
            return GoalCommandResult.Ok($"Goal 已处于终态 {goal.Status}。", goal.ToSnapshot());

        if (!GoalStateMachine.CanTransition(goal.Status, GoalPhase.Cancelled))
            return GoalCommandResult.Fail(
                GoalErrorCodes.InvalidState,
                $"Goal 处于 {goal.Status}，不能 cancel。",
                goal.ToSnapshot());

        var (mutated, _) = await store.TryMutateAsync(
            goal.GoalRunId,
            request.ExpectedVersion ?? 0,
            g =>
            {
                g.Status = GoalPhase.Cancelled;
                g.StatusReason = request.Command.Reason;
                g.ActivationEpoch++;
                g.TerminalAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                return true;
            },
            new GoalRunStore.GoalEventAppend(
                GoalEventTypes.Cancelled,
                new { reason = request.Command.Reason }),
            request.ClientRequestId,
            ct);

        return mutated is null
            ? VersionConflict(goal)
            : GoalCommandResult.Ok(
                "Goal 已取消（可审计终态）。事件、Iteration 与证据不删除。",
                mutated.ToSnapshot());
    }

    private async Task<GoalCommandResult> HandleClearAsync(
        GoalCommandRequest request, CancellationToken ct)
    {
        var latest = await store.FindLatestAsync(request.WorkspaceId, request.ConversationId, ct);
        if (latest is null)
            return GoalCommandResult.Ok("当前会话没有 Goal，无需清除。", EmptySnapshot(request));

        // ADR-074 §11：active Goal 不允许被"藏起来"静默运行 —— 必须先 pause/cancel。
        if (!GoalStateMachine.IsTerminal(latest.Status))
        {
            return GoalCommandResult.Fail(
                GoalErrorCodes.InvalidState,
                "存在非终态 Goal；clear 只清除已结束 Goal 的展示。请先 /goal pause 或 /goal cancel。",
                latest.ToSnapshot());
        }

        var (mutated, _) = await store.TryMutateAsync(
            latest.GoalRunId,
            request.ExpectedVersion ?? 0,
            g =>
            {
                g.ClearedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                return true;
            },
            new GoalRunStore.GoalEventAppend(GoalEventTypes.Cleared, new { }),
            request.ClientRequestId,
            ct);

        return mutated is null
            ? VersionConflict(latest)
            : GoalCommandResult.Ok(
                "Goal 展示已清除（clear 不删除事件、Iteration、Verification 或 Artifact）。",
                mutated.ToSnapshot());
    }

    /// <summary>
    /// 解析 --rounds：命令显式值或配置默认值；配置越界（如 DefaultMaxIterations>256）
    /// fail closed 返回确定性错误，绝不静默使用越界预算。
    /// </summary>
    private bool TryResolveRounds(
        GoalCommandRequest request, out int rounds, out GoalCommandResult error)
    {
        rounds = request.Command.Rounds ?? options.Value.DefaultMaxIterations;
        if (GoalLimits.IsValidIterationBudget(rounds))
        {
            error = null!;
            return true;
        }

        error = GoalCommandResult.Fail(
            GoalErrorCodes.InvalidRounds,
            $"解析后的 Iteration 预算 {rounds} 越界（允许 {GoalLimits.MinIterations}..{GoalLimits.MaxIterationsHardLimit}）；" +
            "请检查 --rounds 或 GoalRuns:DefaultMaxIterations 配置。");
        return false;
    }

    private GoalCommandResult VersionConflict(GoalRunEntity goal)
        => GoalCommandResult.Fail(
            GoalErrorCodes.VersionConflict,
            $"Goal 已被并发修改（当前 aggregate version {goal.AggregateVersion}）；请刷新后重试。",
            goal.ToSnapshot());

    /// <summary>设计方案 §15.3 风格的 presentation；客户端不得解析它驱动按钮。</summary>
    internal static string BuildStatusMessage(GoalRunEntity goal)
    {
        var phase = goal.Status switch
        {
            GoalPhase.Active => "active",
            GoalPhase.Paused => "paused",
            GoalPhase.Blocked => "blocked",
            GoalPhase.BudgetExhausted => "budget exhausted",
            GoalPhase.Completed => "completed",
            GoalPhase.Cancelled => "cancelled",
            GoalPhase.Failed => "failed",
            _ => goal.Status.ToString().ToLowerInvariant(),
        };

        var lines = new List<string>
        {
            $"Goal {phase} · iteration {goal.IterationsStarted}/{goal.MaxIterations}",
            $"Objective: {goal.Objective}",
        };

        if (!string.IsNullOrWhiteSpace(goal.BlockedCode))
            lines.Add($"Blocked: {goal.BlockedCode} - {goal.BlockedMessage}");
        if (!string.IsNullOrWhiteSpace(goal.StatusReason))
            lines.Add($"Reason: {goal.StatusReason}");
        if (!string.IsNullOrWhiteSpace(goal.LastNextAction))
            lines.Add($"Next: {goal.LastNextAction}");

        var commands = GoalStateMachine.IsTerminal(goal.Status)
            ? "Commands: /goal <objective> 创建新 Goal"
            : goal.Status == GoalPhase.Active
                ? "Commands: /goal pause · /goal cancel · /goal edit"
                : "Commands: /goal resume · /goal cancel · /goal edit";
        lines.Add(commands);

        return string.Join('\n', lines);
    }

    private static GoalSnapshot EmptySnapshot(GoalCommandRequest request) => new()
    {
        GoalRunId = string.Empty,
        WorkspaceId = request.WorkspaceId,
        ConversationId = request.ConversationId,
        AgentInstanceId = request.AgentInstanceId,
        Objective = string.Empty,
        ObjectiveVersion = 0,
        Phase = GoalPhase.Cancelled,
        MaxIterations = GoalLimits.DefaultMaxIterations,
        IterationsStarted = 0,
        IterationsSettled = 0,
        ActivationEpoch = 0,
        AggregateVersion = 0,
        CreatedAtUtc = DateTimeOffset.MinValue,
        UpdatedAtUtc = DateTimeOffset.MinValue,
    };
}
