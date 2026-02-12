using System.Collections.Concurrent;
using System.Text.Json;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// Session 状态持久化存储 — 重启后恢复会话状态。
///
/// 设计原则：
///   每次会话状态变更时异步写入磁盘。
///   重启时扫描并恢复所有已知会话的状态。
///   已完成/已关闭的会话保持不变，被中断的运行中会话标记 Stopped。
///
/// 存储位置：data/sessions/{sessionId}.json
/// </summary>
public sealed class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<SessionStateStore>? _logger;

    public SessionStateStore(
        PuddingDataPaths paths,
        ILogger<SessionStateStore>? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>获取存储根目录。</summary>
    private string StoreRoot => Path.Combine(_paths.DataRoot, "sessions");

    /// <summary>获取单个会话的状态文件路径。</summary>
    private string StateFilePath(string sessionId) =>
        Path.Combine(StoreRoot, $"{sessionId}.json");

    /// <summary>
    /// 持久化单个会话的状态到磁盘。
    /// 每次状态变更时由 SessionStateManager 调用。
    /// </summary>
    public async Task PersistAsync(
        string sessionId,
        string state,
        string workspaceId,
        string agentId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            Directory.CreateDirectory(StoreRoot);

            var entry = new SessionStateEntry
            {
                SessionId = sessionId,
                State = state,
                WorkspaceId = workspaceId,
                AgentId = agentId,
                LastActiveAt = DateTimeOffset.UtcNow.ToString("O"),
            };

            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await File.WriteAllTextAsync(StateFilePath(sessionId), json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "[SessionStateStore] Persist failed session={Session} state={State}",
                sessionId, state);
        }
    }

    /// <summary>
    /// 启动时扫描磁盘，加载所有已知会话的状态。
    /// 返回按 sessionId 索引的条目集合。
    /// </summary>
    public IEnumerable<SessionStateEntry> LoadFromDisk()
    {
        if (!Directory.Exists(StoreRoot))
            yield break;

        foreach (var file in Directory.GetFiles(StoreRoot, "*.json"))
        {
            SessionStateEntry? entry = null;
            try
            {
                var json = File.ReadAllText(file);
                entry = JsonSerializer.Deserialize<SessionStateEntry>(json, JsonOpts);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[SessionStateStore] Failed to load {File}", file);
            }

            if (entry is not null && !string.IsNullOrWhiteSpace(entry.SessionId))
                yield return entry;
        }
    }
}

/// <summary>磁盘上的会话状态条目（最小元数据）。</summary>
public sealed record SessionStateEntry
{
    public string SessionId { get; init; } = "";
    public string State { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string LastActiveAt { get; init; } = "";
}
