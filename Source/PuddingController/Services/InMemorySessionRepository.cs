using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>Session 仓储；以本地 JSON 文件持久化，并用内存索引加速读取与查询。</summary>
public sealed class InMemorySessionRepository : ISessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _sessionsRoot;

    public InMemorySessionRepository(PuddingDataPaths? paths = null)
    {
        var dataPaths = paths ?? PuddingDataPaths.FromRoot(Path.Combine(AppContext.BaseDirectory, "data"));
        _sessionsRoot = Path.Combine(dataPaths.RuntimeRoot, "sessions");
        LoadPersistedSessions();
    }

    public async Task<SessionRecord> CreateAsync(SessionRecord record, CancellationToken ct = default)
    {
        _sessions[record.SessionId] = record;
        await PersistAsync(record, ct);
        return record;
    }

    public Task<SessionRecord?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        return Task.FromResult(_sessions.TryGetValue(sessionId, out var record) ? record : null);
    }

    public Task<SessionRecord?> FindActiveAsync(
        string channelId, string ownerUserId, string workspaceId, string agentTemplateId,
        CancellationToken ct = default)
    {
        var session = _sessions.Values
            .Where(s => s.WorkspaceId == workspaceId
                     && s.ChannelId == channelId
                     && s.OwnerUserId == ownerUserId
                     && s.AgentTemplateId == agentTemplateId
                     && s.Status == SessionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();
        return Task.FromResult(session);
    }

    public async Task<SessionRecord?> FindMainAsync(
        string workspaceId,
        string principalKind,
        string principalId,
        CancellationToken ct = default)
    {
        var sessions = await QueryAsync(workspaceId: workspaceId, ct: ct);
        return sessions
            .Where(s => s.SessionRole == SessionRole.Main)
            .Where(s => string.Equals(s.PrincipalKind, principalKind, StringComparison.OrdinalIgnoreCase))
            .Where(s => string.Equals(s.PrincipalId, principalId, StringComparison.Ordinal))
            .OrderByDescending(s => s.LastActiveAt)
            .FirstOrDefault();
    }

    public Task<IReadOnlyList<SessionRecord>> QueryAsync(
        string? channelId = null, string? userId = null, string? workspaceId = null,
        CancellationToken ct = default)
    {
        var query = _sessions.Values.AsEnumerable();
        if (workspaceId is not null)
            query = query.Where(s => s.WorkspaceId == workspaceId);
        if (channelId is not null)
            query = query.Where(s => s.ChannelId == channelId);
        if (userId is not null)
            query = query.Where(s => s.OwnerUserId == userId);

        return Task.FromResult<IReadOnlyList<SessionRecord>>(query.OrderByDescending(s => s.CreatedAt).ToList());
    }

    public async Task UpdateAsync(SessionRecord record, CancellationToken ct = default)
    {
        _sessions[record.SessionId] = record;
        await PersistAsync(record, ct);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryRemove(sessionId, out var existing);
        await _writeLock.WaitAsync(ct);
        try
        {
            if (existing is not null)
            {
                var path = SessionFilePath(existing.WorkspaceId, sessionId);
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            if (!Directory.Exists(_sessionsRoot))
                return;

            foreach (var path in Directory.EnumerateFiles(_sessionsRoot, SessionFileName(sessionId), SearchOption.AllDirectories))
                File.Delete(path);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(SessionRecord record, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await AtomicFileWriter.WriteJsonAsync(SessionFilePath(record.WorkspaceId, record.SessionId), record, JsonOptions, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void LoadPersistedSessions()
    {
        if (!Directory.Exists(_sessionsRoot))
            return;

        foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var record = JsonSerializer.Deserialize<SessionRecord>(stream, JsonOptions);
                if (record is not null && !string.IsNullOrWhiteSpace(record.SessionId))
                    _sessions[record.SessionId] = record;
            }
            catch (JsonException)
            {
                // 单个损坏文件不应阻止服务启动，后续可通过日志/诊断定位该文件。
            }
            catch (IOException)
            {
                // 启动期文件被短暂占用时跳过，避免阻塞 Controller 初始化。
            }
        }
    }

    private string SessionFilePath(string workspaceId, string sessionId) =>
        Path.Combine(_sessionsRoot, WorkspaceSegment(workspaceId), SessionFileName(sessionId));

    private static string WorkspaceSegment(string workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId) ? "_default" : SafeFileToken(workspaceId);

    private static string SessionFileName(string sessionId) => $"{SafeFileToken(sessionId)}.json";

    private static string SafeFileToken(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            return value;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
