using System.Collections.Concurrent;
using System.Text.Json;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// Session 重定向存储 — 维护每个 workspace/agent 的"主会话"映射。
///
/// 设计原则：
///   前端不需要维护 SessionID，只需告诉后端"给当前 agent 的主会话发消息"。
///   后端在 compact（手动/自动）后自动更新映射到新 session。
///   持久化到 agents/{agentId}/session-redirect.json，重启后仍然有效。
///
/// Key: "{workspaceId}/{agentId}" → Value: 最新 sessionId
/// </summary>
public sealed class SessionRedirectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ConcurrentDictionary<string, string> _mappings = new();
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<SessionRedirectStore>? _logger;

    public SessionRedirectStore(
        PuddingDataPaths paths,
        ILogger<SessionRedirectStore>? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// compact 后注册/更新主会话映射。
    /// 同时保留旧 session→新 session 链（兼容原有 Resolve 链式查找）。
    /// </summary>
    public void Register(string workspaceId, string agentId, string oldSessionId, string newSessionId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return;
        if (string.IsNullOrWhiteSpace(newSessionId))
            return;
        if (string.Equals(oldSessionId, newSessionId, StringComparison.Ordinal))
            return;

        // 链式重定向：旧 → 新
        if (!string.IsNullOrWhiteSpace(oldSessionId))
            _mappings[oldSessionId] = newSessionId;

        // 主会话映射：{ws}/{agent} → 新 session
        var mainKey = MainKey(workspaceId, agentId);
        _mappings[mainKey] = newSessionId;

        PersistAsync(workspaceId, agentId, newSessionId).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
                _logger?.LogWarning(t.Exception, "[SessionRedirect] Persist failed {Ws}/{Agent} → {Session}", workspaceId, agentId, newSessionId);
        });

        _logger?.LogInformation(
            "[SessionRedirect] registered {Ws}/{Agent} → {Session} (old={OldSession})",
            workspaceId, agentId, newSessionId, oldSessionId);
    }

    /// <summary>
    /// 解析会话 ID。支持：
    /// 1. 直接传 sessionId → 链式查旧→新重定向
    /// 2. 传 "main" → 查 {ws}/{agent} 映射
    /// </summary>
    public string Resolve(string sessionId, string? workspaceId = null, string? agentId = null)
    {
        // 主会话 sentinel
        if (string.Equals(sessionId, "main", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(agentId))
            {
                var mainKey = MainKey(workspaceId, agentId);
                if (_mappings.TryGetValue(mainKey, out var mainSession))
                    return mainSession;
            }
            return sessionId; // fallback: 返回原始 "main"，由下游处理
        }

        // 链式重定向（旧 session ID → 最新）
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = sessionId;
        while (_mappings.TryGetValue(current, out var next))
        {
            if (!visited.Add(current))
                break; // cycle detected
            current = next;
        }
        return current;
    }

    /// <summary>
    /// 获取指定 agent 的主会话 ID（不存在则返回 null）。
    /// </summary>
    public string? GetMainSession(string workspaceId, string agentId)
    {
        var key = MainKey(workspaceId, agentId);
        return _mappings.TryGetValue(key, out var sessionId) ? sessionId : null;
    }

    /// <summary>启动时从持久化文件加载已有映射。</summary>
    public void LoadFromDisk()
    {
        var root = Path.Combine(
            _paths.DataRoot ?? Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT") ?? "data",
            "agents");
        if (!Directory.Exists(root))
            return;

        foreach (var agentDir in Directory.GetDirectories(root))
        {
            var filePath = Path.Combine(agentDir, "session-redirect.json");
            if (!File.Exists(filePath))
                continue;

            try
            {
                var json = File.ReadAllText(filePath);
                var entry = JsonSerializer.Deserialize<SessionRedirectEntry>(json, JsonOpts);
                if (entry is null || string.IsNullOrWhiteSpace(entry.SessionId))
                    continue;

                var agentId = Path.GetFileName(agentDir);
                var key = MainKey(entry.WorkspaceId, agentId);
                _mappings[key] = entry.SessionId;
                _logger?.LogInformation(
                    "[SessionRedirect] loaded from disk {Ws}/{Agent} → {Session}",
                    entry.WorkspaceId, agentId, entry.SessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SessionRedirect] Failed to load {File}", filePath);
            }
        }
    }

    private async Task PersistAsync(string workspaceId, string agentId, string sessionId)
    {
        var agentRoot = _paths.AgentInstanceRoot(agentId);
        Directory.CreateDirectory(agentRoot);

        var filePath = Path.Combine(agentRoot, "session-redirect.json");
        var entry = new SessionRedirectEntry
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
            SessionId = sessionId,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await File.WriteAllTextAsync(filePath, json);
    }

    private static string MainKey(string workspaceId, string agentId) =>
        $"{workspaceId}/{agentId}";

    private sealed class SessionRedirectEntry
    {
        public string WorkspaceId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }
}
