using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Platform;
using StackExchange.Redis;

namespace PuddingController.Services;

/// <summary>
/// Redis Session 仓储——会话为热数据，支持跨实例共享。
/// 键空间：ctrl:session:{id}，索引：ctrl:sessions:ws:{ws} / ctrl:sessions:ch:{ch} / ctrl:sessions:all
/// </summary>
public sealed class InMemorySessionRepository : ISessionRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);

    private readonly IDatabase? _redis;
    private static readonly ConcurrentDictionary<string, SessionRecord> Sessions = new();

    public InMemorySessionRepository(IConnectionMultiplexer? redis = null)
    {
        _redis = redis?.GetDatabase();
    }

    public async Task<SessionRecord> CreateAsync(SessionRecord record, CancellationToken ct = default)
    {
        if (_redis is not null)
        {
            var json = JsonSerializer.Serialize(record, JsonOpts);
            var score = record.CreatedAt.ToUnixTimeMilliseconds();
            var batch = _redis.CreateBatch();
            var t1 = batch.StringSetAsync(SessionKey(record.SessionId), json, SessionTtl);
            var t2 = batch.SortedSetAddAsync(WsIndexKey(record.WorkspaceId), record.SessionId, score);
            var t3 = batch.SortedSetAddAsync(ChIndexKey(record.ChannelId), record.SessionId, score);
            var t4 = batch.SortedSetAddAsync(AllKey, record.SessionId, score);
            batch.Execute();
            await Task.WhenAll(t1, t2, t3, t4);
        }
        else
        {
            Sessions[record.SessionId] = record;
        }

        return record;
    }

    public async Task<SessionRecord?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        if (_redis is not null)
        {
            var json = await _redis.StringGetAsync(SessionKey(sessionId));
            return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<SessionRecord>((string)json!, JsonOpts);
        }

        return Sessions.TryGetValue(sessionId, out var record) ? record : null;
    }

    public async Task<SessionRecord?> FindActiveAsync(
        string channelId, string ownerUserId, string workspaceId, string agentTemplateId,
        CancellationToken ct = default)
    {
        if (_redis is not null)
        {
            var ids = await _redis.SortedSetRangeByScoreAsync(WsIndexKey(workspaceId), order: Order.Descending);
            foreach (var id in ids)
            {
                var s = await GetAsync(id!, ct);
                if (s is not null
                    && s.ChannelId == channelId
                    && s.OwnerUserId == ownerUserId
                    && s.AgentTemplateId == agentTemplateId
                    && s.Status == SessionStatus.Active)
                    return s;
            }
            return null;
        }

        return Sessions.Values
            .Where(s => s.WorkspaceId == workspaceId
                     && s.ChannelId == channelId
                     && s.OwnerUserId == ownerUserId
                     && s.AgentTemplateId == agentTemplateId
                     && s.Status == SessionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<SessionRecord>> QueryAsync(
        string? channelId = null, string? userId = null, string? workspaceId = null,
        CancellationToken ct = default)
    {
        if (_redis is not null)
        {
            RedisValue[] ids;
            if (workspaceId is not null)
                ids = await _redis.SortedSetRangeByScoreAsync(WsIndexKey(workspaceId), order: Order.Descending);
            else if (channelId is not null)
                ids = await _redis.SortedSetRangeByScoreAsync(ChIndexKey(channelId), order: Order.Descending);
            else
                ids = await _redis.SortedSetRangeByScoreAsync(AllKey, order: Order.Descending);

            var results = new List<SessionRecord>();
            foreach (var id in ids)
            {
                var s = await GetAsync(id!, ct);
                if (s is null) continue;
                if (channelId is not null && s.ChannelId != channelId) continue;
                if (userId is not null && s.OwnerUserId != userId) continue;
                results.Add(s);
            }
            return results;
        }

        var query = Sessions.Values.AsEnumerable();
        if (workspaceId is not null)
            query = query.Where(s => s.WorkspaceId == workspaceId);
        if (channelId is not null)
            query = query.Where(s => s.ChannelId == channelId);
        if (userId is not null)
            query = query.Where(s => s.OwnerUserId == userId);

        return query.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public async Task UpdateAsync(SessionRecord record, CancellationToken ct = default)
    {
        if (_redis is not null)
        {
            var json = JsonSerializer.Serialize(record, JsonOpts);
            await _redis.StringSetAsync(SessionKey(record.SessionId), json, SessionTtl);
            return;
        }

        Sessions[record.SessionId] = record;
    }

    private static string SessionKey(string id) => $"ctrl:session:{id}";
    private static string WsIndexKey(string ws) => $"ctrl:sessions:ws:{ws}";
    private static string ChIndexKey(string ch) => $"ctrl:sessions:ch:{ch}";
    private const string AllKey = "ctrl:sessions:all";
}
