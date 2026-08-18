using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

/// <summary>
/// <see cref="ICompositionStore"/> 的 SQLite 实现（P0-5 步骤 1）。
/// 落 <c>CompositionSnapshots</c> 表（MemoryDbContext，与 Sessions/ContextSegments/CompactionCoverageManifests 同库）。
///
/// 语义：
/// - append-only：只插入、不更新、不删除；<see cref="AppendAsync"/> 要求版本严格单调递增，
///   小于等于当前最大版本时返回 false（防重写/防乱序），不抛异常；
/// - 写穿：每次调用直接落库（不缓存、不批量），调用方负责热路径内存缓存；
/// - <see cref="GetLatestAsync"/> 取该 session 最大 CompositionVersion 的记录；
/// - 并发安全由 SQLite 主键 (SessionId, CompositionVersion) 唯一约束兜底（并发同版本插入会撞主键，
///   由调用方捕获 DbUpdateException 或依赖唯一约束冲突判定失败）。
/// </summary>
public sealed class SqliteCompositionStore : ICompositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;

    public SqliteCompositionStore(IDbContextFactory<MemoryDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    /// <inheritdoc />
    public async Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.CompositionSnapshots
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.CompositionVersion)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToRecord(entity);
    }

    /// <inheritdoc />
    public async Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SessionId);
        if (record.CompositionVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(record), record.CompositionVersion, "CompositionVersion 必须 >= 1。");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var currentMax = await db.CompositionSnapshots
            .Where(e => e.SessionId == record.SessionId)
            .Select(e => (long?)e.CompositionVersion)
            .MaxAsync(ct);

        if (currentMax is not null && record.CompositionVersion <= currentMax.Value)
        {
            // append-only：版本必须严格递增；乱序/重写视为追加失败（不抛异常，由调用方决定重试/降级）。
            return false;
        }

        db.CompositionSnapshots.Add(ToEntity(record));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.CompositionSnapshots
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.CompositionVersion)
            .ToListAsync(ct);

        return entities.Select(ToRecord).ToArray();
    }

    // ── 映射 ─────────────────────────────────────────────

    private static CompositionSnapshotEntity ToEntity(SessionCompositionRecord record) => new()
    {
        SessionId = record.SessionId,
        CompositionVersion = record.CompositionVersion,
        SystemPromptHash = record.SystemPromptHash,
        ToolSpecHash = record.ToolSpecHash,
        PrefixHash = record.PrefixHash,
        SkillManifestHash = record.SkillManifestHash,
        SerializationVersion = record.SerializationVersion,
        ToolIds = JsonSerializer.Serialize(record.ToolIds, JsonOptions),
        ChangeReason = record.ChangeReason,
        PermissionEpoch = record.PermissionEpoch,
        CreatedAtUtc = record.CreatedAtUtc.ToUnixTimeMilliseconds(),
        CanonicalSystemPrefixHash = record.CanonicalSystemPrefixHash,
    };

    private static SessionCompositionRecord ToRecord(CompositionSnapshotEntity entity) => new()
    {
        SessionId = entity.SessionId,
        CompositionVersion = entity.CompositionVersion,
        SystemPromptHash = entity.SystemPromptHash,
        ToolSpecHash = entity.ToolSpecHash,
        PrefixHash = entity.PrefixHash,
        SkillManifestHash = entity.SkillManifestHash,
        SerializationVersion = entity.SerializationVersion,
        ToolIds = DeserializeToolIds(entity.ToolIds),
        ChangeReason = entity.ChangeReason,
        PermissionEpoch = entity.PermissionEpoch,
        CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUtc),
        CanonicalSystemPrefixHash = entity.CanonicalSystemPrefixHash,
    };

    private static IReadOnlyList<string> DeserializeToolIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();
        return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
    }
}
