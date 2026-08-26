using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Core;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Files;

/// <summary>
/// ADR-077 §6.2：SQLite 实现的 <see cref="IFileRefStore"/>（<c>llm_provider_file_refs</c>）。
/// <para>
/// 范式 = <see cref="Tasks.SqliteWorkspaceTaskStore"/>：注入 <see cref="IDbContextFactory{PlatformDbContext}"/>，
/// 取 <c>(SqliteConnection)db.Database.GetDbConnection()</c> + <c>BeginTransactionAsync(Serializable)</c>
/// （Microsoft.Data.Sqlite 对 Serializable 发 <c>BEGIN IMMEDIATE</c>），全原始 SQL + 参数化，
/// 唯一键冲突走 <c>INSERT ... ON CONFLICT DO UPDATE</c>（Sqlite 支持），写操作同事务原子提交。
/// 并发语义参考 <see cref="Execution.SqliteExecutionLeaseStore"/>：BEGIN IMMEDIATE + status CAS，
/// 避免同一唯一键并发 SaveAsync 产生重复行 / 状态漂移。
/// </para>
/// <para>
/// Secret（apiKey）永不进入本类；<see cref="ProviderFileRefRecord.RemoteFileId"/>（provider file_id）
/// 只存不打印（ADR-077 §8）。
/// </para>
/// </summary>
public sealed class SqliteProviderFileRefStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TimeProvider timeProvider) : IFileRefStore
{
    private const IsolationLevel TxLevel = IsolationLevel.Serializable;

    private const string Columns = """
        provider_id, credential_epoch, artifact_id, artifact_sha256, remote_file_id,
        bytes, mime_type, expires_at, last_used_at, status, created_at, updated_at
        """;

    /// <inheritdoc />
    public async Task<ProviderFileRefRecord?> TryGetReadyRefAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialEpoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        // 近过期（距 ExpiresAt 不足 5 分钟）也不返回：ADR §6.2「不再分配给新 invocation」。
        var cutoffUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(IFileRefStore.FileRefNearExpirySkewSeconds);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM llm_provider_file_refs
            WHERE provider_id = @providerId
              AND credential_epoch = @credentialEpoch
              AND artifact_sha256 = @artifactSha256
              AND status = @status
              AND expires_at > @cutoff
            LIMIT 1
            """;
        AddParam(cmd, "@providerId", providerId);
        AddParam(cmd, "@credentialEpoch", credentialEpoch);
        AddParam(cmd, "@artifactSha256", artifactSha256);
        AddParam(cmd, "@status", ProviderFileRefStatusWire.Ready);
        AddParam(cmd, "@cutoff", ToSql(cutoffUtc));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<ProviderFileRefRecord> SaveAsync(
        ProviderFileRefRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(TxLevel, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO llm_provider_file_refs
                  ({Columns})
                VALUES
                  (@providerId, @credentialEpoch, @artifactId, @artifactSha256, @remoteFileId,
                   @bytes, @mimeType, @expiresAt, @lastUsedAt, @status, @createdAt, @updatedAt)
                ON CONFLICT(provider_id, credential_epoch, artifact_sha256) DO UPDATE SET
                  artifact_id    = excluded.artifact_id,
                  remote_file_id = excluded.remote_file_id,
                  bytes          = excluded.bytes,
                  mime_type      = excluded.mime_type,
                  expires_at     = excluded.expires_at,
                  last_used_at   = excluded.last_used_at,
                  status         = excluded.status,
                  updated_at     = excluded.updated_at
                """;
            AddParam(cmd, "@providerId", record.ProviderId);
            AddParam(cmd, "@credentialEpoch", record.CredentialEpoch);
            AddParam(cmd, "@artifactId", record.ArtifactId);
            AddParam(cmd, "@artifactSha256", record.ArtifactSha256);
            AddParam(cmd, "@remoteFileId", record.RemoteFileId);
            AddParam(cmd, "@bytes", record.Bytes);
            AddParam(cmd, "@mimeType", record.MimeType);
            AddParam(cmd, "@expiresAt", ToSql(record.ExpiresAt));
            AddParam(cmd, "@lastUsedAt", record.LastUsedAt is { } lastUsed ? ToSql(lastUsed) : null);
            AddParam(cmd, "@status", ProviderFileRefStatusWire.ToWire(record.Status));
            AddParam(cmd, "@createdAt", ToSql(record.CreatedAt));
            AddParam(cmd, "@updatedAt", ToSql(record.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(ct);

            var saved = await ReadByKeyAsync(conn, tx, record.ProviderId, record.CredentialEpoch, record.ArtifactSha256, ct);
            await tx.CommitAsync(ct);
            return saved!;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ProviderFileRefRecord?> UpdateExpiryAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        DateTimeOffset newExpiresAt,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
        => await CasUpdateAsync(
            providerId, credentialEpoch, artifactSha256,
            setSql: "SET expires_at = @newExpiresAt, updated_at = @updatedAt",
            setParam: cmd => AddParam(cmd, "@newExpiresAt", ToSql(newExpiresAt)),
            statusPredicate: "AND status = 'ready'",
            updatedAt, ct);

    /// <inheritdoc />
    public async Task<ProviderFileRefRecord?> MarkExpiredAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
        => await CasUpdateAsync(
            providerId, credentialEpoch, artifactSha256,
            setSql: "SET status = @newStatus, updated_at = @updatedAt",
            setParam: cmd => AddParam(cmd, "@newStatus", ProviderFileRefStatusWire.Expired),
            statusPredicate: "AND status IN ('uploading', 'ready', 'delete_pending')",
            updatedAt, ct);

    /// <inheritdoc />
    public async Task<ProviderFileRefRecord?> MarkDeletePendingAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
        => await CasUpdateAsync(
            providerId, credentialEpoch, artifactSha256,
            setSql: "SET status = @newStatus, updated_at = @updatedAt",
            setParam: cmd => AddParam(cmd, "@newStatus", ProviderFileRefStatusWire.DeletePending),
            statusPredicate: "AND status IN ('uploading', 'ready', 'delete_pending', 'expired', 'failed')",
            updatedAt, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderFileRefRecord>> ListExpiredAsync(
        DateTimeOffset before,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM llm_provider_file_refs
            WHERE status IN (@expiredStatus, @deletePendingStatus)
              AND expires_at <= @before
            ORDER BY expires_at ASC
            LIMIT @limit
            """;
        AddParam(cmd, "@expiredStatus", ProviderFileRefStatusWire.Expired);
        AddParam(cmd, "@deletePendingStatus", ProviderFileRefStatusWire.DeletePending);
        AddParam(cmd, "@before", ToSql(before));
        AddParam(cmd, "@limit", limit);

        var results = new List<ProviderFileRefRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));
        return results.AsReadOnly();
    }

    // ── 私有 helpers ────────────────────────────────────────────

    /// <summary>按唯一键做 status CAS 的 UPDATE；affected=0 视为 CAS 失败返回 null，否则返回更新后记录。</summary>
    private async Task<ProviderFileRefRecord?> CasUpdateAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        string setSql,
        Action<System.Data.Common.DbCommand> setParam,
        string statusPredicate,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialEpoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(TxLevel, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE llm_provider_file_refs
                {setSql}
                WHERE provider_id = @providerId
                  AND credential_epoch = @credentialEpoch
                  AND artifact_sha256 = @artifactSha256
                {statusPredicate}
                """;
            setParam(cmd);
            AddParam(cmd, "@updatedAt", ToSql(updatedAt));
            AddParam(cmd, "@providerId", providerId);
            AddParam(cmd, "@credentialEpoch", credentialEpoch);
            AddParam(cmd, "@artifactSha256", artifactSha256);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var updated = await ReadByKeyAsync(conn, tx, providerId, credentialEpoch, artifactSha256, ct);
            await tx.CommitAsync(ct);
            return updated;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<ProviderFileRefRecord?> ReadByKeyAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM llm_provider_file_refs
            WHERE provider_id = @providerId
              AND credential_epoch = @credentialEpoch
              AND artifact_sha256 = @artifactSha256
            LIMIT 1
            """;
        AddParam(cmd, "@providerId", providerId);
        AddParam(cmd, "@credentialEpoch", credentialEpoch);
        AddParam(cmd, "@artifactSha256", artifactSha256);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    private static ProviderFileRefRecord ReadRecord(System.Data.Common.DbDataReader reader)
        => new(
            ProviderId: reader.GetString(0),
            CredentialEpoch: reader.GetString(1),
            ArtifactId: reader.GetString(2),
            ArtifactSha256: reader.GetString(3),
            RemoteFileId: reader.GetString(4),
            Bytes: reader.GetInt64(5),
            MimeType: reader.GetString(6),
            ExpiresAt: ReadUtc(reader, 7),
            LastUsedAt: reader.IsDBNull(8) ? null : ReadUtc(reader, 8),
            Status: ProviderFileRefStatusWire.FromWire(reader.GetString(9)),
            CreatedAt: ReadUtc(reader, 10),
            UpdatedAt: ReadUtc(reader, 11));

    private static DateTimeOffset ReadUtc(System.Data.Common.DbDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>统一转 UTC 再写 "O" 格式：保证 SQL 字符串比较 == 时间顺序（字典序）。</summary>
    private static string ToSql(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
