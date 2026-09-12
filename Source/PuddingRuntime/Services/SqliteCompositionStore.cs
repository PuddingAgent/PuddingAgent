using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

/// <summary>
/// <see cref="ICompositionStore"/> 的 SQLite 实现（P0-5 步骤 1 / C01-B CAS）。
/// 落 <c>CompositionSnapshots</c> 表（MemoryDbContext，与 Sessions/ContextSegments/CompactionCoverageManifests 同库）。
///
/// 语义：
/// - append-only：只插入、不更新、不删除；
/// - **CAS**：<see cref="AppendAsync"/> 携带 <c>expectedRevision</c>，在单事务内与当前 head 比较，
///   一致才插入。严禁「先查 MAX 再 INSERT」——head 比较与插入必须同事务，否则两个提交者都可能成功；
/// - 结果结构化：Committed / Conflict（回报 expected/actual）/ Unavailable（可重试，不伪装已提交）；
/// - <see cref="GetLatestAsync"/> 取该 session 最大 CompositionVersion 的记录；
/// - 并发同 revision 由主键 (SessionId, CompositionVersion) 兜底：撞主键分类为 Conflict，不抛未处理异常。
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.CompositionSnapshots
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.CompositionVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    /// <inheritdoc />
    public async Task<CompositionAppendResult> AppendAsync(
        SessionCompositionRecord record,
        long expectedRevision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SessionId);
        if (record.CompositionVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(record), record.CompositionVersion, "CompositionVersion 必须 >= 1。");
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), expectedRevision, "expectedRevision 必须 >= 0。");

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 单事务内读取 head：与插入构成真正的 CAS（非「MAX 后 INSERT」的竞态窗口）。
        var head = await db.CompositionSnapshots
            .Where(e => e.SessionId == record.SessionId)
            .Select(e => (long?)e.CompositionVersion)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;

        if (head != expectedRevision)
        {
            // 期望 head 与实际不符 → 明确冲突（tx 未提交即回滚）。
            return CompositionAppendResult.Conflict(expectedRevision, head);
        }

        if (record.CompositionVersion <= head)
        {
            // 目标 revision 已被占用（append-only 不允许重写）→ 冲突语义。
            return CompositionAppendResult.Conflict(expectedRevision, head);
        }

        db.CompositionSnapshots.Add(ToEntity(record));
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // 并发提交者同时通过 head 比较时由主键 (SessionId, CompositionVersion) 兜底 → CAS 冲突，不抛未处理异常。
            return CompositionAppendResult.Conflict(expectedRevision, head);
        }
        catch (SqliteException ex)
        {
            // busy/locked/IO：可重试不可用，绝不伪装已提交（R5）。
            return CompositionAppendResult.Unavailable($"{ex.GetType().Name}: {ex.Message}");
        }

        return CompositionAppendResult.Committed(record.CompositionVersion);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.CompositionSnapshots
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.CompositionVersion)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    // ── 映射 ─────────────────────────────────────────────

    private static CompositionSnapshotEntity ToEntity(SessionCompositionRecord record) => new()
    {
        SessionId = record.SessionId,
        CompositionVersion = record.CompositionVersion,
        ContentId = record.ContentId,
        SystemPromptHash = record.SystemPromptHash,
        ToolSpecHash = record.ToolSpecHash,
        PrefixHash = record.PrefixHash,
        SkillManifestHash = record.SkillManifestHash,
        SerializationVersion = record.SerializationVersion,
        ToolIds = JsonSerializer.Serialize(record.ToolIds, JsonOptions),
        // C01-B-3 AC4-A：工具定义身份（toolId + definitionHash）JSON；null 表示无定义身份证据（历史行/未接线）。
        ToolBindings = SerializeToolBindings(record.ToolBindings),
        ChangeReason = record.ChangeReason,
        PermissionEpoch = record.PermissionEpoch,
        CreatedAtUtc = record.CreatedAtUtc.ToUnixTimeMilliseconds(),
        CanonicalSystemPrefixHash = record.CanonicalSystemPrefixHash,
    };

    private static SessionCompositionRecord ToRecord(CompositionSnapshotEntity entity) => new()
    {
        SessionId = entity.SessionId,
        CompositionVersion = entity.CompositionVersion,
        // 历史行（C01-B 之前）无 ContentId → null：调用方据此判定「无法证明精确内容」，不得谎称精确恢复。
        ContentId = entity.ContentId,
        SystemPromptHash = entity.SystemPromptHash,
        ToolSpecHash = entity.ToolSpecHash,
        PrefixHash = entity.PrefixHash,
        SkillManifestHash = entity.SkillManifestHash,
        SerializationVersion = entity.SerializationVersion,
        ToolIds = DeserializeToolIds(entity.ToolIds),
        // null（历史行/未接线）与空数组语义不同：null = 无法证明定义级精确恢复（不得谎称），空 = 无工具。
        ToolBindings = DeserializeToolBindings(entity.ToolBindings),
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

    /// <summary>序列化工具定义身份绑定；null 保持 NULL（不写成 "[]"，两者语义不同）。</summary>
    private static string? SerializeToolBindings(IReadOnlyList<ToolBinding>? bindings)
        => bindings is null ? null : JsonSerializer.Serialize(bindings, JsonOptions);

    /// <summary>
    /// 反序列化工具定义身份绑定。列值为 NULL / 空白 → 返回 null（= 「无法证明定义级精确恢复」），
    /// 与「绑定为空集合」严格区分（R13/R15）。
    /// </summary>
    private static IReadOnlyList<ToolBinding>? DeserializeToolBindings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<List<ToolBinding>>(json, JsonOptions);
    }
}
