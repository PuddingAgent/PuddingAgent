using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.ExternalApi;

public enum ExternalIdempotencyOutcome
{
    /// <summary>无记录，可以执行；已写入 claim 占位。</summary>
    Proceed,
    /// <summary>同 key 同 body：重放原资源。</summary>
    Replay,
    /// <summary>同 key 不同 body：409。</summary>
    Conflict,
    /// <summary>同 key 并发进行中：409。</summary>
    InProgress,
}

public sealed record ExternalIdempotencyRecord(
    ExternalIdempotencyOutcome Outcome,
    int? ResponseStatus,
    string? ResourceId);

/// <summary>
/// ADR-075 §8.5 简化幂等：key = SHA-256(tokenId + method + canonical route + Idempotency-Key)，
/// request body 另存 SHA-256。claim-then-execute：先插入占位（唯一索引防并发重复），
/// 成功后回填 response status + resource id；重放按 resource id 重新序列化资源返回。
/// 保留期外的 key 顺带清理（默认 7 天）。
/// </summary>
public sealed class ExternalApiIdempotencyStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<ExternalApiIdempotencyStore>? logger = null)
{
    public static string ComputeKeyHash(string tokenId, string method, string route, string idempotencyKey)
    {
        var raw = $"{tokenId}\n{method.ToUpperInvariant()}\n{route}\n{idempotencyKey}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static string ComputeRequestHash(string? requestBody)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody ?? string.Empty)));

    /// <summary>
    /// 尝试认领幂等 key。Replay/Conflict/InProgress 时不得执行 mutation。
    /// Proceed 时已写入占位行，调用方执行成功后必须 CompleteAsync。
    /// </summary>
    public async Task<ExternalIdempotencyRecord> TryClaimAsync(
        string tokenId,
        string method,
        string route,
        string idempotencyKey,
        string? requestBody,
        TimeSpan retention,
        CancellationToken ct = default)
    {
        var keyHash = ComputeKeyHash(tokenId, method, route, idempotencyKey);
        var requestHash = ComputeRequestHash(requestBody);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await CleanupExpiredAsync(db, retention, ct);

        var existing = await db.ExternalApiIdempotency.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotencyKeyHash == keyHash, ct);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                return new ExternalIdempotencyRecord(ExternalIdempotencyOutcome.Conflict, null, null);

            if (existing.ResponseStatus == 0)
                return new ExternalIdempotencyRecord(ExternalIdempotencyOutcome.InProgress, null, null);

            return new ExternalIdempotencyRecord(
                ExternalIdempotencyOutcome.Replay,
                existing.ResponseStatus,
                existing.ResourceId);
        }

        try
        {
            db.ExternalApiIdempotency.Add(new ExternalApiIdempotencyEntity
            {
                IdempotencyKeyHash = keyHash,
                TokenId = tokenId,
                RequestHash = requestHash,
                ResponseStatus = 0,
                ResourceId = null,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return new ExternalIdempotencyRecord(ExternalIdempotencyOutcome.Proceed, null, null);
        }
        catch (DbUpdateException ex)
        {
            // 并发同 key：唯一索引冲突。
            logger?.LogDebug(ex, "[ExternalIdempotency] 并发同 key 认领冲突");
            return new ExternalIdempotencyRecord(ExternalIdempotencyOutcome.InProgress, null, null);
        }
    }

    public async Task CompleteAsync(
        string tokenId,
        string method,
        string route,
        string idempotencyKey,
        int responseStatus,
        string? resourceId,
        CancellationToken ct = default)
    {
        var keyHash = ComputeKeyHash(tokenId, method, route, idempotencyKey);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ExternalApiIdempotency
            .FirstOrDefaultAsync(x => x.IdempotencyKeyHash == keyHash, ct);
        if (entity is null)
            return;

        entity.ResponseStatus = responseStatus;
        entity.ResourceId = resourceId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>mutation 失败时释放占位，允许调用方重试。</summary>
    public async Task ReleaseAsync(
        string tokenId,
        string method,
        string route,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var keyHash = ComputeKeyHash(tokenId, method, route, idempotencyKey);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ExternalApiIdempotency
            .Where(x => x.IdempotencyKeyHash == keyHash)
            .ExecuteDeleteAsync(ct);
    }

    private static async Task CleanupExpiredAsync(
        PlatformDbContext db,
        TimeSpan retention,
        CancellationToken ct)
    {
        // EF SQLite 不翻译 DateTimeOffset 参数比较；表有界（保留期内 mutation 数），内存过滤后按键删除。
        var cutoff = DateTimeOffset.UtcNow - retention;
        var keys = await db.ExternalApiIdempotency.AsNoTracking()
            .Select(x => new { x.IdempotencyKeyHash, x.CreatedAtUtc })
            .ToListAsync(ct);
        var expired = keys.Where(x => x.CreatedAtUtc < cutoff)
            .Select(x => x.IdempotencyKeyHash)
            .ToList();
        if (expired.Count > 0)
        {
            await db.ExternalApiIdempotency
                .Where(x => expired.Contains(x.IdempotencyKeyHash))
                .ExecuteDeleteAsync(ct);
        }
    }
}
