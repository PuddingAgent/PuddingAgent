using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Platform;
using StackExchange.Redis;

namespace PuddingController.Services;

/// <summary>Redis 审批服务——审批单为临时数据，自动 24h 迎来TTL。</summary>
public sealed class InMemoryApprovalService : IApprovalService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(24);
    private const string PendingSetKey = "ctrl:approvals:pending";

    private readonly IDatabase _redis;

    public InMemoryApprovalService(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    public async Task<ApprovalRecord> RequestApprovalAsync(
        string sessionId, string workspaceId, string actionDescription,
        CancellationToken ct = default)
    {
        var record = new ApprovalRecord
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            ActionDescription = actionDescription,
            ConfirmationCode = Guid.NewGuid().ToString("N")[..8],
            ExpiresAt = DateTimeOffset.UtcNow.Add(DefaultExpiry),
        };
        var json = JsonSerializer.Serialize(record, JsonOpts);
        var batch = _redis.CreateBatch();
        var t1 = batch.StringSetAsync(ApprovalKey(record.ApprovalId), json, DefaultExpiry);
        var t2 = batch.SetAddAsync(PendingSetKey, record.ApprovalId);
        batch.Execute();
        await Task.WhenAll(t1, t2);
        return record;
    }

    public async Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default)
    {
        var json = await _redis.StringGetAsync(ApprovalKey(approvalId));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<ApprovalRecord>((string)json!, JsonOpts);
    }

    public async Task<IReadOnlyList<ApprovalRecord>> QueryPendingAsync(CancellationToken ct = default)
    {
        var ids = await _redis.SetMembersAsync(PendingSetKey);
        var now = DateTimeOffset.UtcNow;
        var results = new List<ApprovalRecord>();
        var staleIds = new List<RedisValue>();

        foreach (var id in ids)
        {
            var record = await GetAsync(id!, ct);
            if (record is null) { staleIds.Add(id); continue; }
            if (record.Status != ApprovalStatus.Pending
                || (record.ExpiresAt is not null && record.ExpiresAt <= now))
            {
                staleIds.Add(id);
                continue;
            }
            results.Add(record);
        }
        if (staleIds.Count > 0)
            await _redis.SetRemoveAsync(PendingSetKey, [.. staleIds]);

        return results.OrderByDescending(a => a.CreatedAt).ToList();
    }

    public async Task<bool> ConfirmAsync(
        string approvalId, string confirmationCode, string confirmedBy,
        CancellationToken ct = default)
    {
        var record = await GetAsync(approvalId, ct);
        if (record is null || record.Status != ApprovalStatus.Pending) return false;

        if (record.ExpiresAt is not null && record.ExpiresAt < DateTimeOffset.UtcNow)
        {
            await PersistAsync(record with { Status = ApprovalStatus.Expired });
            await _redis.SetRemoveAsync(PendingSetKey, approvalId);
            return false;
        }
        if (record.ConfirmationCode != confirmationCode) return false;

        var confirmed = record with
        {
            Status = ApprovalStatus.Confirmed,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedBy = confirmedBy,
        };
        var batch = _redis.CreateBatch();
        var t1 = batch.StringSetAsync(ApprovalKey(approvalId), JsonSerializer.Serialize(confirmed, JsonOpts));
        var t2 = batch.SetRemoveAsync(PendingSetKey, approvalId);
        batch.Execute();
        await Task.WhenAll(t1, t2);
        return true;
    }

    public async Task<bool> RejectAsync(string approvalId, string rejectedBy, CancellationToken ct = default)
    {
        var record = await GetAsync(approvalId, ct);
        if (record is null || record.Status != ApprovalStatus.Pending) return false;

        var rejected = record with
        {
            Status = ApprovalStatus.Rejected,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedBy = rejectedBy,
        };
        var batch = _redis.CreateBatch();
        var t1 = batch.StringSetAsync(ApprovalKey(approvalId), JsonSerializer.Serialize(rejected, JsonOpts));
        var t2 = batch.SetRemoveAsync(PendingSetKey, approvalId);
        batch.Execute();
        await Task.WhenAll(t1, t2);
        return true;
    }

    private Task PersistAsync(ApprovalRecord record)
        => _redis.StringSetAsync(ApprovalKey(record.ApprovalId), JsonSerializer.Serialize(record, JsonOpts));

    private static string ApprovalKey(string id) => $"ctrl:approval:{id}";
}