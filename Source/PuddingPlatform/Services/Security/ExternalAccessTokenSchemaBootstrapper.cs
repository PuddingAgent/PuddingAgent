using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Security;

/// <summary>
/// ADR-075: external_access_tokens / _scopes / _workspaces / _audit_events 的幂等 SQLite schema bootstrap。
/// 与 WorkspaceTaskSchemaBootstrapper 同风格：EF EnsureCreated 覆盖全新库，本 bootstrap 覆盖已有库
/// （CREATE TABLE IF NOT EXISTS + 幂等索引）。列名与 Data.Entities.ExternalAccessToken* 的 [Column] 严格一致。
/// </summary>
public static class ExternalAccessTokenSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        // ── external_access_tokens（13 列）────────────────────────
        """
        CREATE TABLE IF NOT EXISTS external_access_tokens (
            token_id            TEXT    NOT NULL,
            key_id              TEXT    NOT NULL,
            secret_hash         BLOB    NOT NULL,
            display_prefix      TEXT    NOT NULL,
            name                TEXT    NOT NULL,
            owner_user_id       TEXT    NOT NULL,
            version             INTEGER NOT NULL DEFAULT 1,
            created_at_utc      TEXT    NOT NULL,
            expires_at_utc      TEXT    NOT NULL,
            revoked_at_utc      TEXT,
            revoked_by_user_id  TEXT,
            revocation_reason   TEXT,
            last_used_at_utc    TEXT,
            PRIMARY KEY (token_id)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_external_access_tokens_key_id ON external_access_tokens(key_id);",
        "CREATE INDEX IF NOT EXISTS IX_external_access_tokens_owner ON external_access_tokens(owner_user_id);",

        // ── external_access_token_scopes（联合主键）─────────────────
        """
        CREATE TABLE IF NOT EXISTS external_access_token_scopes (
            token_id TEXT NOT NULL,
            scope    TEXT NOT NULL,
            PRIMARY KEY (token_id, scope)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_external_access_token_scopes_scope ON external_access_token_scopes(scope);",

        // ── external_access_token_workspaces（联合主键）─────────────
        """
        CREATE TABLE IF NOT EXISTS external_access_token_workspaces (
            token_id     TEXT NOT NULL,
            workspace_id TEXT NOT NULL,
            PRIMARY KEY (token_id, workspace_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_external_access_token_workspaces_workspace ON external_access_token_workspaces(workspace_id);",

        // ── external_access_token_audit_events（append-only）────────
        """
        CREATE TABLE IF NOT EXISTS external_access_token_audit_events (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id        TEXT    NOT NULL,
            token_id        TEXT    NOT NULL,
            key_id          TEXT    NOT NULL,
            event_type      INTEGER NOT NULL,
            reason          TEXT,
            actor           TEXT,
            occurred_at_utc TEXT    NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_external_access_token_audit_event_id ON external_access_token_audit_events(event_id);",
        "CREATE INDEX IF NOT EXISTS IX_external_access_token_audit_events_token ON external_access_token_audit_events(token_id, occurred_at_utc);",
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
                    "[ExternalAccessTokenSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
