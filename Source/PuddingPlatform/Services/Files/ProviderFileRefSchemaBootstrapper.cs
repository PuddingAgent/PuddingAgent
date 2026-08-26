using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Files;

/// <summary>
/// ADR-077 §6.2：<c>llm_provider_file_refs</c> 幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="Tasks.WorkspaceTaskSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖全新库，
/// 本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS + 幂等索引）。列名与
/// <see cref="Data.Entities.ProviderFileRefEntity"/> 的 [Column] 严格一致；
/// 时间存 DateTimeOffset（TEXT ISO8601 "O" 格式）、status 存 wire 字符串。
/// </para>
/// </summary>
public static class ProviderFileRefSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        // ── llm_provider_file_refs（ADR-077 §6.2，12 列）─────────────
        """
        CREATE TABLE IF NOT EXISTS llm_provider_file_refs (
            provider_id      TEXT    NOT NULL,
            credential_epoch TEXT    NOT NULL,
            artifact_id      TEXT    NOT NULL,
            artifact_sha256  TEXT    NOT NULL,
            remote_file_id   TEXT    NOT NULL,
            bytes            INTEGER NOT NULL,
            mime_type        TEXT    NOT NULL,
            expires_at       TEXT    NOT NULL,
            last_used_at     TEXT,
            status           TEXT    NOT NULL,
            created_at       TEXT    NOT NULL,
            updated_at       TEXT    NOT NULL,
            PRIMARY KEY (provider_id, credential_epoch, artifact_sha256)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_llm_provider_file_refs_status ON llm_provider_file_refs(status);",
        "CREATE INDEX IF NOT EXISTS IX_llm_provider_file_refs_expires_at ON llm_provider_file_refs(expires_at);",
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        foreach (var ddl in Ddl)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "[ProviderFileRefSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
