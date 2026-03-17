using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent Session 管理器——管理 Runtime 内所有活跃的 Agent 实例。
/// </summary>
public sealed class AgentSessionManager
{
    private readonly ConcurrentDictionary<string, AgentInstanceRecord> _instances = new();

    /// <summary>获取或创建 Agent 实例。</summary>
    public AgentInstanceRecord GetOrCreate(string sessionId, string agentTemplateId)
    {
        return _instances.GetOrAdd(sessionId, _ => new AgentInstanceRecord
        {
            AgentInstanceId = Guid.NewGuid().ToString("N"),
            AgentTemplateId = agentTemplateId,
            SessionId = sessionId,
            Status = AgentInstanceStatus.Running,
        });
    }

    /// <summary>获取实例。</summary>
    public AgentInstanceRecord? Get(string sessionId) =>
        _instances.GetValueOrDefault(sessionId);

    /// <summary>更新实例活跃时间。</summary>
    public void Touch(string sessionId)
    {
        if (_instances.TryGetValue(sessionId, out var inst))
        {
            _instances[sessionId] = inst with { LastActiveAt = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>终止实例。</summary>
    public void Terminate(string sessionId)
    {
        if (_instances.TryGetValue(sessionId, out var inst))
        {
            _instances[sessionId] = inst with { Status = AgentInstanceStatus.Terminated };
        }
    }

    /// <summary>列出所有活跃实例。</summary>
    public IReadOnlyList<AgentInstanceRecord> ListActive() =>
        _instances.Values.Where(i => i.Status == AgentInstanceStatus.Running).ToList();

    /// <summary>移除实例记录（用于超时清理）。</summary>
    public void Remove(string sessionId) =>
        _instances.TryRemove(sessionId, out _);
}
