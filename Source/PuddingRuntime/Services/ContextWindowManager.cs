using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingCode.Services;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services;

/// <summary>
/// 上下文窗口与会话历史管理器。
/// 负责内存历史、DB/JSONL 回填、历史裁剪与过期清理。
/// </summary>
public sealed class ContextWindowManager
{
    private static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromHours(1);

    private readonly AgentSessionManager _sessionManager;
    private readonly InMemoryRuntimeSessionStore _runtimeSessionStore;
    private readonly ExecutionControlRegistry _controlRegistry;
    private readonly ExecutionJournal _journal;
    private readonly IDbContextFactory<MemoryDbContext>? _memoryDbFactory;
    private readonly JsonlSessionReader? _jsonlReader;
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _historyLastAccessedAt;
    private readonly ConcurrentDictionary<string, TimeSpan> _historyTimeouts;
    private readonly ConcurrentDictionary<string, int> _activeExecutions;
    private readonly ILogger<ContextWindowManager> _logger;

    public ContextWindowManager(
        AgentSessionManager sessionManager,
        InMemoryRuntimeSessionStore runtimeSessionStore,
        ExecutionControlRegistry controlRegistry,
        ExecutionJournal journal,
        ILogger<ContextWindowManager> logger,
        IDbContextFactory<MemoryDbContext>? memoryDbFactory = null,
        JsonlSessionReader? jsonlReader = null)
    {
        _sessionManager = sessionManager;
        _runtimeSessionStore = runtimeSessionStore;
        _controlRegistry = controlRegistry;
        _journal = journal;
        _logger = logger;
        _memoryDbFactory = memoryDbFactory;
        _jsonlReader = jsonlReader;

        _histories = new ConcurrentDictionary<string, List<ChatMessage>>();
        _historyLastAccessedAt = new ConcurrentDictionary<string, DateTimeOffset>();
        _historyTimeouts = new ConcurrentDictionary<string, TimeSpan>();
        _activeExecutions = new ConcurrentDictionary<string, int>();
    }

    public List<ChatMessage> GetOrCreateHistory(string sessionId)
    {
        return _histories.GetOrAdd(sessionId, _ => []);
    }

    public void TouchHistoryAccess(string sessionId, TimeSpan sessionTimeout)
    {
        _historyLastAccessedAt[sessionId] = DateTimeOffset.UtcNow;
        _historyTimeouts[sessionId] = NormalizeSessionTimeout(sessionTimeout);
    }

    public void MarkSessionExecuting(string sessionId)
    {
        _activeExecutions.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);
    }

    public void MarkSessionExecutionCompleted(string sessionId)
    {
        while (true)
        {
            if (!_activeExecutions.TryGetValue(sessionId, out var current))
                return;

            if (current <= 1)
            {
                _activeExecutions.TryRemove(sessionId, out _);
                return;
            }

            if (_activeExecutions.TryUpdate(sessionId, current - 1, current))
                return;
        }
    }

    public void CleanupExpiredSessions(string protectedSessionId)
    {
        CleanupExpiredHistories(protectedSessionId);

        var expiredSessions = _sessionManager.CleanupExpired(
            protectedSessionId,
            shouldSkip: IsSessionExecuting);

        if (expiredSessions.Count == 0)
            return;

        foreach (var sessionId in expiredSessions)
        {
            _histories.TryRemove(sessionId, out _);
            _historyLastAccessedAt.TryRemove(sessionId, out _);
            _historyTimeouts.TryRemove(sessionId, out _);

            _runtimeSessionStore.Terminate(sessionId, "timeout");
            _controlRegistry.Remove(sessionId);
            _journal.ClearAnchor(sessionId);
        }

        _logger.LogInformation(
            "[AgentExec] Cleanup removed {Count} expired sessions (protected={ProtectedSession})",
            expiredSessions.Count,
            protectedSessionId);
    }

    private void CleanupExpiredHistories(string protectedSessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var removedCount = 0;

        foreach (var pair in _historyLastAccessedAt)
        {
            var sessionId = pair.Key;
            if (string.Equals(sessionId, protectedSessionId, StringComparison.Ordinal))
                continue;

            if (IsSessionExecuting(sessionId))
                continue;

            if (_sessionManager.IsWaitingEvent(sessionId))
                continue;

            var timeout = NormalizeSessionTimeout(_historyTimeouts.GetValueOrDefault(sessionId));
            if (now - pair.Value <= timeout)
                continue;

            if (_histories.TryRemove(sessionId, out _))
                removedCount++;

            _historyLastAccessedAt.TryRemove(sessionId, out _);
            _historyTimeouts.TryRemove(sessionId, out _);
        }

        if (removedCount > 0)
        {
            _logger.LogInformation(
                "[AgentExec] Cleanup removed {Count} expired history entries (protected={ProtectedSession})",
                removedCount,
                protectedSessionId);
        }
    }

    private bool IsSessionExecuting(string sessionId) =>
        _activeExecutions.ContainsKey(sessionId);

    /// <summary>
    /// 从数据库构建会话上下文窗口（优先供 SSE 流式路径使用）。
    /// V1：按 token 预算（默认 8000）从新到旧取候选，再按旧到新组装，跳过已压缩消息。
    /// </summary>
    public async Task<List<ChatMessage>> BuildContextFromDbAsync(
        string sessionId,
        int maxTokenBudget = 8000,
        CancellationToken ct = default)
    {
        if (_memoryDbFactory is null)
            return [];

        await using var db = await _memoryDbFactory.CreateDbContextAsync(ct);
        var entities = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.CompactedBy == null)
            .OrderByDescending(m => m.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var messages = new List<ChatMessage>(entities.Count);
        var estimatedTokens = 0;

        for (var i = entities.Count - 1; i >= 0; i--)
        {
            var entity = entities[i];
            var content = entity.Content ?? string.Empty;

            // 简化估算：约 1 token ≈ 3 字符（中英混排折中），最少按 1 token 计。
            var tokenEstimate = Math.Max(1, content.Length / 3);
            if (estimatedTokens + tokenEstimate > maxTokenBudget && messages.Count > 2)
                break;

            estimatedTokens += tokenEstimate;
            messages.Add(new ChatMessage(
                ParseChatRole(entity.Role),
                content,
                ToolCallId: null,
                ToolCalls: null,
                ReasoningContent: entity.ThinkingJson));
        }

        return messages;
    }

    /// <summary>
    /// SSE 路径优先按数据库重建上下文；失败时保持现有内存历史不变。
    /// </summary>
    public async Task TryHydrateStreamHistoryFromDbAsync(
        string sessionId,
        List<ChatMessage> history,
        int maxTokenBudget,
        CancellationToken ct)
    {
        try
        {
            List<ChatMessage>? hydrated = null;

            if (_memoryDbFactory is not null)
            {
                var dbHistory = await BuildContextFromDbAsync(sessionId, maxTokenBudget, ct);
                if (dbHistory.Count > 0)
                {
                    hydrated = dbHistory;
                }
            }

            if (hydrated is null && _jsonlReader is not null)
            {
                var jsonlHistory = await BuildContextFromJsonlAsync(sessionId, maxTokenBudget, ct);
                if (jsonlHistory.Count > 0)
                {
                    hydrated = jsonlHistory;
                }
            }

            if (hydrated is null || hydrated.Count == 0)
                return;

            history.Clear();
            history.AddRange(hydrated.Where(m => m.Role != ChatRole.System));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[AgentExec] Build context from db/jsonl failed; fallback to in-memory history. session={Session}",
                sessionId);
        }
    }

    public async Task<List<ChatMessage>> BuildContextFromJsonlAsync(
        string sessionId,
        int maxTokenBudget,
        CancellationToken ct)
    {
        if (_jsonlReader is null)
            return [];

        var entries = await _jsonlReader.ReadSessionAsync(sessionId, ct);
        if (entries.Count == 0)
            return [];

        var selected = new List<ChatMessage>();
        var estimatedTokens = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            var content = entry.Content ?? string.Empty;
            var tokenEstimate = Math.Max(1, content.Length / 3);
            if (estimatedTokens + tokenEstimate > maxTokenBudget && selected.Count > 2)
                break;

            estimatedTokens += tokenEstimate;
            selected.Add(new ChatMessage(
                ParseChatRole(entry.Role),
                content,
                ToolCallId: null,
                ToolCalls: null,
                ReasoningContent: entry.ThinkingJson));
        }

        selected.Reverse();
        return selected;
    }

    public async Task TrimHistoryAsync(
        string sessionId,
        List<ChatMessage> history,
        int maxTokenBudget,
        bool preferDbContextWindow,
        CancellationToken ct)
    {
        if (preferDbContextWindow && _memoryDbFactory is not null)
        {
            try
            {
                var dbHistory = await BuildContextFromDbAsync(sessionId, maxTokenBudget, ct);
                if (dbHistory.Count > 0)
                {
                    var system = history.FirstOrDefault(m => m.Role == ChatRole.System);
                    history.Clear();
                    if (system is not null)
                        history.Add(system);
                    history.AddRange(dbHistory.Where(m => m.Role != ChatRole.System));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[AgentExec] TrimHistory db path failed; fallback to legacy trimming. session={Session}",
                    sessionId);
            }
        }

        TrimHistory(history, maxTokenBudget);
    }

    public void TrimHistory(List<ChatMessage> history, int maxTokenBudget)
    {
        const int maxMessages = 40;
        if (history.Count <= maxMessages + 1) return;

        var system = history.FirstOrDefault(m => m.Role == ChatRole.System);
        var recent = history.TakeLast(maxMessages).ToList();
        history.Clear();
        if (system is not null) history.Add(system);
        history.AddRange(recent);
    }

    private static ChatRole ParseChatRole(string role) => role switch
    {
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static TimeSpan NormalizeSessionTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : DefaultSessionTimeout;
}
