using Microsoft.EntityFrameworkCore;
using PuddingCode.Security;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Security;

/// <summary>认证/管理读取用的完整 Token 事实（含 owner 启用状态）。</summary>
public sealed record ExternalAccessTokenRecord
{
    public required string TokenId { get; init; }
    public required string KeyId { get; init; }
    public required byte[] SecretHash { get; init; }
    public required string DisplayPrefix { get; init; }
    public required string Name { get; init; }
    public required string OwnerUserId { get; init; }
    public int Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
    public string? RevokedByUserId { get; init; }
    public string? RevocationReason { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required IReadOnlyList<string> Workspaces { get; init; }
    /// <summary>owner 用户当前是否启用（join AppUsers；owner 被删除视为未启用，fail closed）。</summary>
    public required bool OwnerEnabled { get; init; }

    public ExternalAccessTokenStatus Status
    {
        get
        {
            if (RevokedAtUtc is not null)
                return ExternalAccessTokenStatus.Revoked;
            if (ExpiresAtUtc <= DateTimeOffset.UtcNow)
                return ExternalAccessTokenStatus.Expired;
            return OwnerEnabled
                ? ExternalAccessTokenStatus.Active
                : ExternalAccessTokenStatus.OwnerDisabled;
        }
    }
}

/// <summary>列表行（绝不包含 SecretHash）。</summary>
public sealed record ExternalAccessTokenListItem
{
    public required string TokenId { get; init; }
    public required string KeyId { get; init; }
    public required string DisplayPrefix { get; init; }
    public required string Name { get; init; }
    public required string OwnerUserId { get; init; }
    public int Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
    public string? RevokedByUserId { get; init; }
    public string? RevocationReason { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required IReadOnlyList<string> Workspaces { get; init; }
    public required ExternalAccessTokenStatus Status { get; init; }
}

public sealed record ExternalAccessTokenListFilter
{
    public ExternalAccessTokenStatus? Status { get; init; }
    public string? OwnerUserId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Scope { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public enum ExternalAccessTokenMutationResult
{
    Ok,
    NotFound,
    VersionConflict,
}

/// <summary>
/// ADR-075: External Access Token 持久化。持有 Singleton DbContextFactory，
/// 每次调用创建并释放独立 DbContext（与 TaskAgentCommandService 同模式）。
/// 认证路径只读；revoke/create 为同步持久事实；last-used 由合并器节流写入。
/// </summary>
public sealed class ExternalAccessTokenStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    public async Task<ExternalAccessTokenRecord?> FindByKeyIdAsync(string keyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ExternalAccessTokens
            .Include(t => t.Scopes)
            .Include(t => t.Workspaces)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.KeyId == keyId, ct);
        if (entity is null)
            return null;

        var ownerEnabled = await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.UserId == entity.OwnerUserId && u.IsEnabled, ct);

        return ToRecord(entity, ownerEnabled);
    }

    public async Task<ExternalAccessTokenRecord?> FindByTokenIdAsync(string tokenId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ExternalAccessTokens
            .Include(t => t.Scopes)
            .Include(t => t.Workspaces)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenId == tokenId, ct);
        if (entity is null)
            return null;

        var ownerEnabled = await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.UserId == entity.OwnerUserId && u.IsEnabled, ct);

        return ToRecord(entity, ownerEnabled);
    }

    public async Task CreateAsync(ExternalAccessTokenRecord record, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.ExternalAccessTokens.Add(new ExternalAccessTokenEntity
        {
            TokenId = record.TokenId,
            KeyId = record.KeyId,
            SecretHash = record.SecretHash,
            DisplayPrefix = record.DisplayPrefix,
            Name = record.Name,
            OwnerUserId = record.OwnerUserId,
            Version = 1,
            CreatedAtUtc = record.CreatedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
        });
        db.ExternalAccessTokenScopes.AddRange(record.Scopes.Select(s => new ExternalAccessTokenScopeEntity
        {
            TokenId = record.TokenId,
            Scope = s,
        }));
        db.ExternalAccessTokenWorkspaces.AddRange(record.Workspaces.Select(w => new ExternalAccessTokenWorkspaceEntity
        {
            TokenId = record.TokenId,
            WorkspaceId = w,
        }));
        db.ExternalAccessTokenAuditEvents.Add(CreateAudit(record.TokenId, record.KeyId,
            ExternalAccessTokenAuditEventType.Created, actor: record.OwnerUserId,
            occurredAtUtc: record.CreatedAtUtc));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<(IReadOnlyList<ExternalAccessTokenListItem> Items, int Total)> ListAsync(
        ExternalAccessTokenListFilter filter,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var query =
            from t in db.ExternalAccessTokens.AsNoTracking()
            let ownerEnabled = db.AppUsers.Any(u => u.UserId == t.OwnerUserId && u.IsEnabled)
            select new { t, OwnerEnabled = ownerEnabled };

        if (!string.IsNullOrWhiteSpace(filter.OwnerUserId))
            query = query.Where(x => x.t.OwnerUserId == filter.OwnerUserId);

        if (!string.IsNullOrWhiteSpace(filter.WorkspaceId))
            query = query.Where(x => x.t.Workspaces.Any(w => w.WorkspaceId == filter.WorkspaceId));

        if (!string.IsNullOrWhiteSpace(filter.Scope))
            query = query.Where(x => x.t.Scopes.Any(s => s.Scope == filter.Scope));

        // Revoked/未撤销可在 SQL 过滤；Expired/OwnerDisabled 依赖 DateTimeOffset 参数比较
        // 与 owner join 的派生状态（EF SQLite 不翻译 DateTimeOffset 参数比较），在内存计算。
        // Token 表以 MaxActiveTokensPerOwner 为上界的有界集合，全量物化成本可忽略。
        if (filter.Status is { } requestedStatus)
        {
            query = requestedStatus == ExternalAccessTokenStatus.Revoked
                ? query.Where(x => x.t.RevokedAtUtc != null)
                : query.Where(x => x.t.RevokedAtUtc == null);
        }

        // SQLite EF 不支持 DateTimeOffset ORDER BY/参数比较：排序在内存完成（有界集合）。
        var rows = await query
            .Select(x => new { x.t, x.OwnerEnabled })
            .ToListAsync(ct);

        var materialized = rows
            .Select(r => new { Row = r, Status = ComputeStatus(r.t, r.OwnerEnabled, now) })
            .Where(x => filter.Status is null || x.Status == filter.Status)
            .OrderByDescending(x => x.Row.t.CreatedAtUtc)
            .ToList();

        var total = materialized.Count;

        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var page = Math.Max(filter.Page, 1);
        var paged = materialized
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var tokenIds = paged.Select(r => r.Row.t.TokenId).ToList();
        var scopes = await db.ExternalAccessTokenScopes.AsNoTracking()
            .Where(s => tokenIds.Contains(s.TokenId))
            .ToListAsync(ct);
        var workspaces = await db.ExternalAccessTokenWorkspaces.AsNoTracking()
            .Where(w => tokenIds.Contains(w.TokenId))
            .ToListAsync(ct);

        var items = paged
            .Select(r => new ExternalAccessTokenListItem
            {
                TokenId = r.Row.t.TokenId,
                KeyId = r.Row.t.KeyId,
                DisplayPrefix = r.Row.t.DisplayPrefix,
                Name = r.Row.t.Name,
                OwnerUserId = r.Row.t.OwnerUserId,
                Version = r.Row.t.Version,
                CreatedAtUtc = r.Row.t.CreatedAtUtc,
                ExpiresAtUtc = r.Row.t.ExpiresAtUtc,
                RevokedAtUtc = r.Row.t.RevokedAtUtc,
                RevokedByUserId = r.Row.t.RevokedByUserId,
                RevocationReason = r.Row.t.RevocationReason,
                LastUsedAtUtc = r.Row.t.LastUsedAtUtc,
                Scopes = scopes.Where(s => s.TokenId == r.Row.t.TokenId).Select(s => s.Scope).OrderBy(s => s).ToList(),
                Workspaces = workspaces.Where(w => w.TokenId == r.Row.t.TokenId).Select(w => w.WorkspaceId).OrderBy(w => w).ToList(),
                Status = r.Status,
            })
            .ToList();

        return (items, total);
    }

    public async Task<int> CountActiveAsync(string ownerUserId, CancellationToken ct = default)
    {
        // DateTimeOffset 参数比较在 EF SQLite 不可翻译；取回后内存计数（每 owner 有界）。
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expiryTimes = await db.ExternalAccessTokens.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerUserId && t.RevokedAtUtc == null)
            .Select(t => t.ExpiresAtUtc)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return expiryTimes.Count(e => e > now);
    }

    public async Task<ExternalAccessTokenMutationResult> RenameAsync(
        string tokenId,
        int expectedVersion,
        string newName,
        string actorUserId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ExternalAccessTokens.FirstOrDefaultAsync(t => t.TokenId == tokenId, ct);
        if (entity is null)
            return ExternalAccessTokenMutationResult.NotFound;
        if (entity.Version != expectedVersion)
            return ExternalAccessTokenMutationResult.VersionConflict;

        entity.Name = newName;
        entity.Version++;
        db.ExternalAccessTokenAuditEvents.Add(CreateAudit(entity.TokenId, entity.KeyId,
            ExternalAccessTokenAuditEventType.Renamed, actor: actorUserId));
        await db.SaveChangesAsync(ct);
        return ExternalAccessTokenMutationResult.Ok;
    }

    public async Task<ExternalAccessTokenMutationResult> RevokeAsync(
        string tokenId,
        int expectedVersion,
        string revokedByUserId,
        string? reason,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ExternalAccessTokens.FirstOrDefaultAsync(t => t.TokenId == tokenId, ct);
        if (entity is null)
            return ExternalAccessTokenMutationResult.NotFound;
        if (entity.RevokedAtUtc is not null)
            return ExternalAccessTokenMutationResult.VersionConflict;
        if (entity.Version != expectedVersion)
            return ExternalAccessTokenMutationResult.VersionConflict;

        entity.RevokedAtUtc = DateTimeOffset.UtcNow;
        entity.RevokedByUserId = revokedByUserId;
        entity.RevocationReason = reason;
        entity.Version++;
        db.ExternalAccessTokenAuditEvents.Add(CreateAudit(entity.TokenId, entity.KeyId,
            ExternalAccessTokenAuditEventType.Revoked, reason: reason, actor: revokedByUserId));
        await db.SaveChangesAsync(ct);
        return ExternalAccessTokenMutationResult.Ok;
    }

    /// <summary>last-used 合并写（由 UsageCoalescer 调用；不在认证热路径）。</summary>
    public async Task TouchLastUsedAsync(string tokenId, DateTimeOffset usedAtUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ExternalAccessTokens.FirstOrDefaultAsync(t => t.TokenId == tokenId, ct);
        if (entity is null || entity.LastUsedAtUtc is not null && entity.LastUsedAtUtc >= usedAtUtc)
            return;

        entity.LastUsedAtUtc = usedAtUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task AppendAuditAsync(
        string tokenId,
        string keyId,
        ExternalAccessTokenAuditEventType eventType,
        string? reason = null,
        string? actor = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ExternalAccessTokenAuditEvents.Add(CreateAudit(tokenId, keyId, eventType, reason, actor));
        await db.SaveChangesAsync(ct);
    }

    private static ExternalAccessTokenAuditEventEntity CreateAudit(
        string tokenId,
        string keyId,
        ExternalAccessTokenAuditEventType eventType,
        string? reason = null,
        string? actor = null,
        DateTimeOffset? occurredAtUtc = null)
        => new()
        {
            EventId = $"tevt_{Guid.NewGuid():N}",
            TokenId = tokenId,
            KeyId = keyId,
            EventType = eventType,
            Reason = reason is { Length: > 64 } ? reason[..64] : reason,
            Actor = actor,
            OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
        };

    private static ExternalAccessTokenStatus ComputeStatus(
        ExternalAccessTokenEntity entity,
        bool ownerEnabled,
        DateTimeOffset now)
    {
        if (entity.RevokedAtUtc is not null)
            return ExternalAccessTokenStatus.Revoked;
        if (entity.ExpiresAtUtc <= now)
            return ExternalAccessTokenStatus.Expired;
        return ownerEnabled
            ? ExternalAccessTokenStatus.Active
            : ExternalAccessTokenStatus.OwnerDisabled;
    }

    private static ExternalAccessTokenRecord ToRecord(ExternalAccessTokenEntity entity, bool ownerEnabled)
        => new()
        {
            TokenId = entity.TokenId,
            KeyId = entity.KeyId,
            SecretHash = entity.SecretHash,
            DisplayPrefix = entity.DisplayPrefix,
            Name = entity.Name,
            OwnerUserId = entity.OwnerUserId,
            Version = entity.Version,
            CreatedAtUtc = entity.CreatedAtUtc,
            ExpiresAtUtc = entity.ExpiresAtUtc,
            RevokedAtUtc = entity.RevokedAtUtc,
            RevokedByUserId = entity.RevokedByUserId,
            RevocationReason = entity.RevocationReason,
            LastUsedAtUtc = entity.LastUsedAtUtc,
            Scopes = entity.Scopes.Select(s => s.Scope).OrderBy(s => s).ToList(),
            Workspaces = entity.Workspaces.Select(w => w.WorkspaceId).OrderBy(w => w).ToList(),
            OwnerEnabled = ownerEnabled,
        };
}
