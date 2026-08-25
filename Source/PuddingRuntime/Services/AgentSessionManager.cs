using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent Session 管理器——管理 Runtime 内所有活跃的 Agent 实例。
/// </summary>
public sealed class AgentSessionManager
{
    // 与 AgentExecutionService.DefaultSessionTimeout 保持一致（4h）：
    // 减少日间空闲后的全量重水合；模板 runtime.sessionTimeout 可覆盖。
    private static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromHours(4);

    private readonly ConcurrentDictionary<string, AgentInstanceRecord> _instances = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccessedAt = new();
    private readonly ConcurrentDictionary<string, TimeSpan> _sessionTimeouts = new();
    private readonly ConcurrentDictionary<string, byte> _waitingEventSessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _loadedToolIds = new();
    private readonly ILogger<AgentSessionManager>? _logger;

    public AgentSessionManager(ILogger<AgentSessionManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>获取或创建 Agent 实例。</summary>
    public AgentInstanceRecord GetOrCreate(
        string sessionId,
        string agentTemplateId,
        TimeSpan? sessionTimeout = null,
        string? preferredAgentInstanceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var instance = _instances.GetOrAdd(sessionId, _ => new AgentInstanceRecord
        {
            AgentInstanceId = string.IsNullOrWhiteSpace(preferredAgentInstanceId)
                ? Guid.NewGuid().ToString("N")
                : preferredAgentInstanceId.Trim(),
            AgentTemplateId = agentTemplateId,
            SessionId = sessionId,
            Status = AgentInstanceStatus.Running,
            LastActiveAt = now,
        });

        _lastAccessedAt[sessionId] = now;
        if (sessionTimeout is { } configuredTimeout && configuredTimeout > TimeSpan.Zero)
            _sessionTimeouts[sessionId] = configuredTimeout;
        else
            _sessionTimeouts.TryAdd(sessionId, DefaultSessionTimeout);

        // 为每个 Agent 实例创建独立工作目录
        var agentWorkDir = Path.Combine(AppContext.BaseDirectory, "data", "agents", agentTemplateId);
        try { Directory.CreateDirectory(agentWorkDir); } catch { /* best effort */ }

        return instance;
    }

    /// <summary>获取实例。</summary>
    public AgentInstanceRecord? Get(string sessionId) =>
        _instances.GetValueOrDefault(sessionId);

    /// <summary>更新实例活跃时间。</summary>
    public void Touch(string sessionId)
    {
        if (_instances.TryGetValue(sessionId, out var inst))
        {
            var now = DateTimeOffset.UtcNow;
            _instances[sessionId] = inst with { LastActiveAt = now };
            _lastAccessedAt[sessionId] = now;
        }
    }

    /// <summary>标记会话进入 WaitingEvent（等待外部事件，不参与过期清理）。</summary>
    public void MarkWaitingEvent(string sessionId)
    {
        Touch(sessionId);
        _waitingEventSessions[sessionId] = 0;
    }

    /// <summary>标记会话回到 Running（清除 WaitingEvent 保护）。</summary>
    public void MarkRunning(string sessionId)
    {
        _waitingEventSessions.TryRemove(sessionId, out _);
        if (_instances.TryGetValue(sessionId, out var inst))
        {
            var now = DateTimeOffset.UtcNow;
            _instances[sessionId] = inst with
            {
                Status = AgentInstanceStatus.Running,
                LastActiveAt = now,
            };
            _lastAccessedAt[sessionId] = now;
        }
    }

    /// <summary>会话是否处于 WaitingEvent 保护态。</summary>
    public bool IsWaitingEvent(string sessionId) =>
        _waitingEventSessions.ContainsKey(sessionId);

    /// <summary>
    /// 惰性清理超时会话。
    /// WaitingEvent 会话默认不会被清理，避免打断等待中的恢复链路。
    /// </summary>
    public IReadOnlyList<string> CleanupExpired(
        string? protectedSessionId = null,
        Func<string, bool>? shouldSkip = null)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = new List<string>();

        foreach (var pair in _lastAccessedAt)
        {
            var sessionId = pair.Key;
            if (string.Equals(sessionId, protectedSessionId, StringComparison.Ordinal))
                continue;

            if (shouldSkip?.Invoke(sessionId) == true)
                continue;

            if (_waitingEventSessions.ContainsKey(sessionId))
                continue;

            var timeout = NormalizeTimeout(_sessionTimeouts.GetValueOrDefault(sessionId));
            if (now - pair.Value <= timeout)
                continue;

            if (_instances.TryGetValue(sessionId, out var inst))
            {
                _instances[sessionId] = inst with
                {
                    Status = AgentInstanceStatus.Terminated,
                    LastActiveAt = now,
                };
            }

            // 工具集合 append-only：清理实例不清理已授权工具面。
            // 1h 超时只回收会话实例，避免下一轮工具可见集缩回 core schema
            // 导致 provider prefix 漂移；跨重启持久化水合由 P0-5 步骤 5 负责。
            Remove(sessionId);
            removed.Add(sessionId);
        }

        if (removed.Count > 0)
        {
            _logger?.LogInformation(
                "[AgentSessionManager] Cleaned up {Count} expired sessions (protected={ProtectedSession})",
                removed.Count,
                protectedSessionId ?? "(none)");
        }

        return removed;
    }

    /// <summary>终止实例。</summary>
    public void Terminate(string sessionId)
    {
        if (_instances.TryGetValue(sessionId, out var inst))
        {
            _instances[sessionId] = inst with { Status = AgentInstanceStatus.Terminated };
        }

        _waitingEventSessions.TryRemove(sessionId, out _);
    }

    /// <summary>列出所有活跃实例。</summary>
    public IReadOnlyList<AgentInstanceRecord> ListActive() =>
        _instances.Values.Where(i => i.Status == AgentInstanceStatus.Running).ToList();

    /// <summary>
    /// Returns the progressively discovered tool surface for this live session.
    /// Keeping it across dispatches prevents every user turn from shrinking to the
    /// core schema set and then invalidating the provider prefix when tools reload.
    /// </summary>
    public HashSet<string> GetLoadedToolIds(string sessionId)
    {
        if (!_loadedToolIds.TryGetValue(sessionId, out var tools))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return tools.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Persists newly discovered tool ids for the lifetime of the session.</summary>
    public void RememberLoadedToolIds(string sessionId, IEnumerable<string> toolIds)
    {
        var tools = _loadedToolIds.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        foreach (var toolId in toolIds)
        {
            if (!string.IsNullOrWhiteSpace(toolId))
                tools.TryAdd(toolId.Trim(), 0);
        }
    }

    /// <summary>
    /// P0-5 步骤 5：从持久化 Composition 记录水合工具集合（跨 1h 超时 / Core 重启恢复）。
    /// append-only 语义：只追加持久化 toolIds，不覆盖/收缩进程内已有集合。
    /// </summary>
    public void HydrateToolIds(string sessionId, IEnumerable<string> toolIds) =>
        RememberLoadedToolIds(sessionId, toolIds);

    /// <summary>
    /// Returns an immutable snapshot of the session's progressively discovered tool
    /// surface. The set is append-only and survives session cleanup; callers receive a
    /// copy, so mutations cannot affect the internal state. Returns an empty set when
    /// the session has no recorded tools. (P0-5 step 5 will use this to hydrate the
    /// committed tool set across restarts.)
    /// </summary>
    public IReadOnlySet<string> SnapshotToolSet(string sessionId)
    {
        if (!_loadedToolIds.TryGetValue(sessionId, out var tools))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return tools.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>移除实例记录（用于超时清理）。</summary>
    public void Remove(string sessionId) =>
        RemoveInternal(sessionId);

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : DefaultSessionTimeout;

    private void RemoveInternal(string sessionId)
    {
        _instances.TryRemove(sessionId, out _);
        _lastAccessedAt.TryRemove(sessionId, out _);
        _sessionTimeouts.TryRemove(sessionId, out _);
        _waitingEventSessions.TryRemove(sessionId, out _);
        // 工具集合 append-only：不删除已授权工具面（见 CleanupExpired 注释），
        // 保证进程内跨 1h 超时工具可见集不收缩回 core schema。
    }
}
