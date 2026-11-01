using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Autodream 触发钩子：Loop 结束后判定是否需要整理记忆，需要则把任务交给后台执行器。
/// <para>
/// 三条硬约束（与 Hook 契约一致）：
/// ① **不阻塞主链路** —— 只在判定通过时投递一次任务，实际工作全在后台；
/// ② **可关闭** —— 由 <c>PUDDING_MEMORY_DREAM</c> 开关控制，默认关闭；
/// ③ **失败静默** —— 任何异常只记日志，绝不影响主执行链。
/// </para>
/// <para>
/// 本类只负责「判定 + 投递」，**不直接读写记忆**。真正的整理由
/// <see cref="IMemoryDreamExecutor"/> 的实现承担（子代理 / 后台任务），
/// 这样触发策略与执行策略可以各自替换、各自测试。
/// </para>
/// </summary>
public sealed class MemoryDreamSchedulerHook : IAgentLoopHook
{
    private readonly MemoryDreamPolicy _policy;
    private readonly IMemoryDreamExecutor? _executor;
    private readonly ILogger<MemoryDreamSchedulerHook>? _logger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DreamState> _state = new(StringComparer.Ordinal);

    public MemoryDreamSchedulerHook(
        MemoryDreamPolicy? policy = null,
        IMemoryDreamExecutor? executor = null,
        ILogger<MemoryDreamSchedulerHook>? logger = null,
        TimeProvider? clock = null)
    {
        _policy = policy ?? MemoryDreamPolicy.Default;
        _executor = executor;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>开关：默认关闭，只有显式设置 <c>PUDDING_MEMORY_DREAM=1</c> 才启用。</summary>
    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("PUDDING_MEMORY_DREAM"), "1", StringComparison.Ordinal);

    /// <summary>供测试观察：本实例投递过多少次任务。</summary>
    public int DispatchCount { get; private set; }

    public Task OnLoopCompleteAsync(
        AgentLoopContext context,
        string finalMessage,
        AgentLoopStopReason stopReason,
        CancellationToken ct = default)
    {
        if (!Enabled || _executor is null || context is null)
            return Task.CompletedTask;

        try
        {
            var key = context.SessionId;
            var now = _clock.GetUtcNow();

            DreamState state;
            lock (_state)
            {
                if (!_state.TryGetValue(key, out state))
                    state = new DreamState();
            }

            if (!_policy.ShouldDream(stopReason, state.LastDreamAt, now, state.Count))
                return Task.CompletedTask;

            lock (_state)
            {
                _state[key] = new DreamState
                {
                    LastDreamAt = now,
                    Count = state.Count + 1,
                };
            }

            DispatchCount++;
            _ = DispatchAsync(context, stopReason, ct);
        }
        catch (Exception ex)
        {
            // 记忆整理是"锦上添花"，不能因为它把一次正常结束变成故障。
            _logger?.LogWarning(ex, "[MemoryDream] 触发判定失败，已跳过 session={Session}", context.SessionId);
        }

        return Task.CompletedTask;
    }

    private async Task DispatchAsync(
        AgentLoopContext context,
        AgentLoopStopReason stopReason,
        CancellationToken ct)
    {
        try
        {
            await _executor!.RunAsync(
                new MemoryDreamRequest(
                    SessionId: context.SessionId,
                    WorkspaceId: context.WorkspaceId,
                    AgentInstanceId: context.AgentInstanceId,
                    StopReason: stopReason,
                    RequestedAt: _clock.GetUtcNow()),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MemoryDream] 后台整理失败（已忽略）session={Session}", context.SessionId);
        }
    }

    private readonly record struct DreamState(DateTimeOffset? LastDreamAt, int Count)
    {
        public DreamState() : this(null, 0) { }
    }
}

/// <summary>一次整理请求。字段都是"是哪一个会话/工作区"，不含内容 —— 内容由执行器自己去读。</summary>
public sealed record MemoryDreamRequest(
    string SessionId,
    string WorkspaceId,
    string AgentInstanceId,
    AgentLoopStopReason StopReason,
    DateTimeOffset RequestedAt);

/// <summary>
/// 整理执行器。刻意做成接口：真正的实现要用子代理 + 记忆图组件，
/// 而触发策略（本目录）不应依赖它的任何细节。
/// </summary>
public interface IMemoryDreamExecutor
{
    Task RunAsync(MemoryDreamRequest request, CancellationToken ct = default);
}
