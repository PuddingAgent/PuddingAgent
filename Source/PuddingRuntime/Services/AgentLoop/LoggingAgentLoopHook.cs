using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 内置默认 Hook：将 Agent Loop 各阶段事件写入结构化日志。
/// 注册后无需任何配置即可提供完整 Loop 可观测性。
/// </summary>
public sealed class LoggingAgentLoopHook : IAgentLoopHook
{
    private readonly ILogger<LoggingAgentLoopHook> _logger;

    public LoggingAgentLoopHook(ILogger<LoggingAgentLoopHook> logger)
        => _logger = logger;

    public Task OnLoopStartAsync(AgentLoopContext ctx, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AgentLoop] START session={Session} agent={Agent} maxRounds={Max}",
            ctx.SessionId, ctx.AgentInstanceId, ctx.MaxRounds);
        return Task.CompletedTask;
    }

    public Task OnRoundStartAsync(AgentLoopContext ctx, int round, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[AgentLoop] ROUND {Round}/{Max} session={Session}",
            round + 1, ctx.MaxRounds, ctx.SessionId);
        return Task.CompletedTask;
    }

    public Task OnToolCallAsync(
        AgentLoopContext ctx, int round, string toolName, string argsJson,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AgentLoop] TOOL_CALL round={Round} tool={Tool} args={Args}",
            round + 1, toolName,
            argsJson.Length > 120 ? argsJson[..120] + "…" : argsJson);
        return Task.CompletedTask;
    }

    public Task OnToolResultAsync(
        AgentLoopContext ctx, int round, string toolName, SkillResult result,
        CancellationToken ct = default)
    {
        if (result.Success)
            _logger.LogInformation(
                "[AgentLoop] TOOL_RESULT round={Round} tool={Tool} ok exitCode={Exit} outLen={Len}",
                round + 1, toolName, result.ExitCode, result.Output?.Length ?? 0);
        else
            _logger.LogWarning(
                "[AgentLoop] TOOL_RESULT round={Round} tool={Tool} FAILED error={Error}",
                round + 1, toolName, result.Error);
        return Task.CompletedTask;
    }

    public Task OnRoundCompleteAsync(
        AgentLoopContext ctx, int round, AgentLoopResponse response,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[AgentLoop] ROUND_DONE round={Round} status={Status} hasTool={HasTool}",
            round + 1, response.Status, response.Tool?.Name is not null);
        return Task.CompletedTask;
    }

    public Task OnLoopCompleteAsync(
        AgentLoopContext ctx, string finalMessage, AgentLoopStopReason stopReason,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AgentLoop] COMPLETE session={Session} stopReason={Reason} msgLen={Len}",
            ctx.SessionId, stopReason, finalMessage.Length);
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync(
        AgentLoopContext ctx, string finalMessage, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AgentLoop] COMPLETED session={Session} msgLen={Len}",
            ctx.SessionId, finalMessage.Length);
        return Task.CompletedTask;
    }

    public Task OnCancelledAsync(AgentLoopContext ctx, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "[AgentLoop] CANCELLED session={Session} agent={Agent}",
            ctx.SessionId, ctx.AgentInstanceId);
        return Task.CompletedTask;
    }

    public Task OnWaitingAsync(
        AgentLoopContext ctx, AgentLoopResponse response, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AgentLoop] WAITING session={Session} reason={Reason}",
            ctx.SessionId, response.Meta?.Reason);
        return Task.CompletedTask;
    }

    public Task OnFailedAsync(
        AgentLoopContext ctx, string reason, Exception? ex, CancellationToken ct = default)
    {
        if (ex is not null)
            _logger.LogError(ex,
                "[AgentLoop] FAILED session={Session} reason={Reason}",
                ctx.SessionId, reason);
        else
            _logger.LogWarning(
                "[AgentLoop] FAILED session={Session} reason={Reason}",
                ctx.SessionId, reason);
        return Task.CompletedTask;
    }

    public Task OnMaxRoundsReachedAsync(AgentLoopContext ctx, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "[AgentLoop] MAX_ROUNDS_REACHED session={Session} maxRounds={Max}",
            ctx.SessionId, ctx.MaxRounds);
        return Task.CompletedTask;
    }

    public Task OnLoopErrorAsync(AgentLoopContext ctx, Exception ex, CancellationToken ct = default)
    {
        _logger.LogError(ex,
            "[AgentLoop] ERROR session={Session} agent={Agent}",
            ctx.SessionId, ctx.AgentInstanceId);
        return Task.CompletedTask;
    }
}
