using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.AgentLoop;

// ── 上下文 ────────────────────────────────────────────────────────────────

/// <summary>Agent Loop 执行期间传递给所有 Hook 的只读上下文。</summary>
public sealed class AgentLoopContext
{
    public required string SessionId       { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string WorkspaceId     { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string UserMessage     { get; init; }
    /// <summary>本次 Loop 允许的最大轮次（含工具调用）。</summary>
    public required int    MaxRounds       { get; init; }
}

// ── 停止原因 ──────────────────────────────────────────────────────────────

public enum AgentLoopStopReason
{
    /// <summary>Agent 在响应中输出了 status=DONE。</summary>
    Done,
    /// <summary>已达到最大轮次或总耗时上限，强制停止。</summary>
    MaxRoundsReached,
    /// <summary>外部 CancellationToken 被取消，或执行控制面下发冻结/取消指令。</summary>
    Cancelled,
    /// <summary>Agent 在响应中输出了 status=WAIT，进入挂起等待态。</summary>
    Waiting,
    /// <summary>Agent 在响应中输出了 status=FAILED，或执行过程中抛出未处理异常。</summary>
    Failed,
    /// <summary>总执行时间超过 MaxElapsed 限制。</summary>
    MaxElapsedReached,
}

// ── Hook 接口 ─────────────────────────────────────────────────────────────

/// <summary>
/// Agent Loop 生命周期 Hook。
/// 所有方法均提供默认空实现，只需覆盖感兴趣的节点。
/// </summary>
public interface IAgentLoopHook
{
    /// <summary>Loop 启动前（首次 LLM 调用之前）。</summary>
    Task OnLoopStartAsync(AgentLoopContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>每轮 LLM 调用开始前。round 从 0 计。</summary>
    Task OnRoundStartAsync(AgentLoopContext context, int round, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>LLM 响应已解析，准备执行工具调用前触发。</summary>
    Task OnToolCallAsync(
        AgentLoopContext context,
        int round,
        string toolName,
        string argsJson,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>工具调用执行完毕后触发。</summary>
    Task OnToolResultAsync(
        AgentLoopContext context,
        int round,
        string toolName,
        SkillResult result,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>每轮 LLM 响应解析完成后触发（包含 DONE/CONTINUE 等结构信息）。</summary>
    Task OnRoundCompleteAsync(
        AgentLoopContext context,
        int round,
        AgentLoopResponse response,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Loop 以任意原因结束后触发（供通用收口处理）。</summary>
    Task OnLoopCompleteAsync(
        AgentLoopContext context,
        string finalMessage,
        AgentLoopStopReason stopReason,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>任务成功完成（status=DONE + CompletionPolicy 通过）后触发。</summary>
    Task OnCompletedAsync(
        AgentLoopContext context,
        string finalMessage,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>执行被取消或冻结后触发。</summary>
    Task OnCancelledAsync(
        AgentLoopContext context,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Agent 发出 WAIT 信号，进入等待态时触发。</summary>
    Task OnWaitingAsync(
        AgentLoopContext context,
        AgentLoopResponse response,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>执行因异常或 Agent FAILED 信号而失败时触发。</summary>
    Task OnFailedAsync(
        AgentLoopContext context,
        string reason,
        Exception? exception,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>达到最大轮次或总耗时上限时触发。</summary>
    Task OnMaxRoundsReachedAsync(
        AgentLoopContext context,
        CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Loop 因未捕获异常终止时触发。</summary>
    Task OnLoopErrorAsync(
        AgentLoopContext context,
        Exception exception,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
