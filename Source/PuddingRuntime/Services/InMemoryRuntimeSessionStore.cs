using System.Collections.Concurrent;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services;

/// <summary>内存 Runtime 会话状态存储——跟踪所有活跃 Agent 会话。</summary>
public sealed class InMemoryRuntimeSessionStore
{
    private readonly ConcurrentDictionary<string, SessionRuntimeRecord> _sessions = new();

    /// <summary>登记或更新一条 Runtime 会话记录。</summary>
    public SessionRuntimeRecord GetOrCreate(string sessionId, string agentInstanceId, string workspaceId, string templateId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new SessionRuntimeRecord
        {
            SessionId = sessionId,
            AgentInstanceId = agentInstanceId,
            WorkspaceId = workspaceId,
            AgentTemplateId = templateId,
        });
    }

    /// <summary>更新心跳时间并递增轮次。</summary>
    public void Touch(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var record))
        {
            record.LastActiveAt = DateTimeOffset.UtcNow;
            record.TurnCount++;
        }
    }

    /// <summary>标记会话为非活跃。</summary>
    public void Terminate(string sessionId, string reason)
    {
        if (_sessions.TryGetValue(sessionId, out var record))
        {
            record.IsActive = false;
            record.TerminationReason = reason;
        }
    }

    /// <summary>获取所有活跃会话。</summary>
    public IReadOnlyList<SessionRuntimeRecord> GetAll()
        => _sessions.Values.ToList();

    /// <summary>获取所有超时的活跃会话（超过指定时长未活跃）。</summary>
    public IReadOnlyList<SessionRuntimeRecord> GetExpired(TimeSpan timeout)
    {
        var cutoff = DateTimeOffset.UtcNow - timeout;
        return _sessions.Values
            .Where(s => s.IsActive && s.LastActiveAt < cutoff)
            .ToList();
    }

    public SessionRuntimeRecord? Get(string sessionId)
        => _sessions.GetValueOrDefault(sessionId);
}
