using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// 「Agent 自主开始 / 停止 / 取消 Goal」的平台层实现。只复用 canonical 命令路径
/// （<see cref="IGoalCommandService"/>.ExecuteAsync → GoalCommandService 的
/// HandleSetAsync / HandlePauseAsync / HandleCancelAsync），不复制状态机判断、不直写 goal_runs、
/// 不新增第二条创建或终结路径（与 <see cref="GoalResumeService"/> 同构）。
/// <para>
/// 归属强制：Set/Pause/Cancel 三个分支在 GoalCommandService 内一律经
/// <c>GoalRunStore.FindActiveAsync(conversationId, agentInstanceId)</c> 定位，跨会话/跨 Agent
/// 没有可达路径，因此本适配层不复制归属判断。
/// </para>
/// <para>
/// 权能边界（刻意不提供）：<see cref="GoalCommandKind.Extend"/>（延长额度）与 Policy/Clear
/// 在契约中已标注为人工权能（GoalContracts.cs Extend 注释原文「仅用户 slash / HTTP 入口，
/// 不暴露为 agent 侧工具」），故本服务与工具层都不代理。Agent 遇到 budget_exhausted 时只能
/// 用 Start 新建 Goal，或交由人工 extend —— 这是 fail-closed 的设计边界，不是缺口。
/// </para>
/// <para>
/// 生命周期：本服务注册为 Singleton（与 GoalResumeService 一致），每次调用创建独立 scope
/// 解析 scoped 的 <see cref="IGoalCommandService"/>，避免 captive dependency。
/// </para>
/// </summary>
public sealed class GoalLifecycleService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalLifecycleService> logger) : IGoalLifecycleService
{
    /// <inheritdoc />
    public async Task<GoalLifecycleResult> ExecuteAsync(
        GoalLifecycleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);

        if (!TryBuildCommand(request, out var command, out var invalid))
            return invalid;

        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId)
            ? BuildDeterministicCommandId(request)
            : request.ClientRequestId;

        // 工具/服务为 singleton，canonical Goal 存储使用 scoped DbContext：
        // 每次调用保持一个独立 scope 存活。
        await using var scope = scopeFactory.CreateAsyncScope();
        var goalCommands = scope.ServiceProvider.GetRequiredService<IGoalCommandService>();

        GoalCommandResult result;
        try
        {
            result = await goalCommands.ExecuteAsync(
                new GoalCommandRequest(
                    request.WorkspaceId,
                    request.ConversationId,
                    request.AgentInstanceId,
                    request.UserId,
                    clientRequestId,
                    command,
                    request.SourceChannel ?? GoalLifecycleCodes.AgentToolSourceChannel),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[GoalLifecycle] failed action={Action} conv={ConversationId} workspace={WorkspaceId}",
                request.Action,
                request.ConversationId,
                request.WorkspaceId);
            return new GoalLifecycleResult
            {
                Success = false,
                Code = GoalLifecycleCodes.InternalError,
                Message = "Goal 生命周期命令执行失败，请查看后端诊断日志。",
            };
        }

        return result.Success
            ? MapSuccess(request.Action, result)
            : MapFailure(result);
    }

    private static bool TryBuildCommand(
        GoalLifecycleRequest request,
        out GoalCommand command,
        out GoalLifecycleResult failure)
    {
        failure = null!;

        switch (request.Action)
        {
            case GoalLifecycleAction.Start:
            {
                var objective = request.Objective?.Trim();
                if (string.IsNullOrWhiteSpace(objective)
                    || objective.Length > GoalLimits.ObjectiveMaxLength)
                {
                    command = null!;
                    failure = Fail(
                        GoalLifecycleCodes.InvalidObjective,
                        $"开始 Goal 需要 1–{GoalLimits.ObjectiveMaxLength} 字符的 objective"
                        + $"（当前 {objective?.Length ?? 0} 字符）。");
                    return false;
                }

                if (request.Rounds is { } rounds && !GoalLimits.IsValidIterationBudget(rounds))
                {
                    command = null!;
                    failure = Fail(
                        GoalLifecycleCodes.InvalidRounds,
                        $"rounds 必须是 {GoalLimits.MinIterations}..{GoalLimits.MaxIterationsHardLimit} "
                        + $"之间的整数（当前 {rounds}）。");
                    return false;
                }

                command = new GoalCommand
                {
                    Kind = GoalCommandKind.Set,
                    Objective = objective,
                    Rounds = request.Rounds,
                };
                return true;
            }

            case GoalLifecycleAction.Pause:
            case GoalLifecycleAction.Cancel:
            {
                var reason = request.Reason?.Trim();
                if (reason is { Length: > GoalLimits.ObjectiveMaxLength })
                {
                    command = null!;
                    failure = Fail(
                        GoalLifecycleCodes.InvalidObjective,
                        $"reason 超过 {GoalLimits.ObjectiveMaxLength} 字符上限。");
                    return false;
                }

                command = new GoalCommand
                {
                    Kind = request.Action == GoalLifecycleAction.Pause
                        ? GoalCommandKind.Pause
                        : GoalCommandKind.Cancel,
                    Reason = string.IsNullOrEmpty(reason) ? null : reason,
                };
                return true;
            }

            default:
                command = null!;
                failure = Fail(
                    GoalErrorCodes.InvalidCommand,
                    $"未知的 Goal 生命周期动作 '{request.Action}'。");
                return false;
        }
    }

    /// <summary>
    /// 缺省幂等键：由 (action, conversation, agent, 内容摘要) 确定性派生。
    /// 同一意图重投（例如工具层重试）不会创建第二个 Goal —— HandleSetAsync 按 clientRequestId
    /// 幂等重放首次结果。
    /// </summary>
    private static string BuildDeterministicCommandId(GoalLifecycleRequest request)
    {
        var material = string.Join(
            '\u001f',
            request.Action.ToString(),
            request.ConversationId,
            request.AgentInstanceId,
            request.Objective?.Trim() ?? string.Empty,
            request.Rounds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.Reason?.Trim() ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"goal-tool:{request.Action}:{Convert.ToHexString(hash)[..32]}";
    }

    private static GoalLifecycleResult MapSuccess(
        GoalLifecycleAction action,
        GoalCommandResult result)
    {
        var snapshot = result.Snapshot;
        return new GoalLifecycleResult
        {
            Success = true,
            Code = action switch
            {
                GoalLifecycleAction.Start => GoalLifecycleCodes.Started,
                GoalLifecycleAction.Pause => GoalLifecycleCodes.Paused,
                _ => GoalLifecycleCodes.Cancelled,
            },
            Message = result.Message,
            GoalRunId = snapshot?.GoalRunId,
            Objective = snapshot?.Objective,
            Phase = snapshot?.Phase,
            BlockedCode = snapshot?.BlockedCode,
            MaxIterations = snapshot?.MaxIterations,
            IterationsStarted = snapshot?.IterationsStarted,
            IterationsSettled = snapshot?.IterationsSettled,
            ActivationEpoch = snapshot?.ActivationEpoch,
            AggregateVersion = snapshot?.AggregateVersion,
        };
    }

    /// <summary>失败一律透传 canonical 路径的稳定 wire code，绝不改写为泛化错误。</summary>
    private static GoalLifecycleResult MapFailure(GoalCommandResult result) =>
        new()
        {
            Success = false,
            Code = string.IsNullOrWhiteSpace(result.ErrorCode)
                ? GoalLifecycleCodes.InternalError
                : result.ErrorCode,
            Message = result.Message,
            GoalRunId = result.Snapshot?.GoalRunId,
            Objective = result.Snapshot?.Objective,
            Phase = result.Snapshot?.Phase,
            BlockedCode = result.Snapshot?.BlockedCode,
            MaxIterations = result.Snapshot?.MaxIterations,
            IterationsStarted = result.Snapshot?.IterationsStarted,
            IterationsSettled = result.Snapshot?.IterationsSettled,
            ActivationEpoch = result.Snapshot?.ActivationEpoch,
            AggregateVersion = result.Snapshot?.AggregateVersion,
        };

    private static GoalLifecycleResult Fail(string code, string message) =>
        new() { Success = false, Code = code, Message = message };
}
