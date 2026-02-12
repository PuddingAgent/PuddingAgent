using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent Session 管理器——管理 Runtime 内所有活跃的 Agent 实例。
/// </summary>
public sealed class AgentSessionManager
{
    private static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, AgentInstanceRecord> _instances = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccessedAt = new();
    private readonly ConcurrentDictionary<string, TimeSpan> _sessionTimeouts = new();
    private readonly ConcurrentDictionary<string, byte> _waitingEventSessions = new();
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
    }
}
