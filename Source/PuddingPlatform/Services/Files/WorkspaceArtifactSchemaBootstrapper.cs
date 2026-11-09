using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Files;

/// <summary>Idempotent SQLite schema bootstrap for versioned workspace artifacts.</summary>
public static class WorkspaceArtifactSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS workspace_artifacts (
            artifact_version_id TEXT NOT NULL PRIMARY KEY,
            workspace_id        TEXT NOT NULL,
            artifact_id         TEXT NOT NULL,
            version             TEXT NOT NULL,
            content_ref         TEXT NOT NULL,
            payload_hash        TEXT NOT NULL,
            created_at          TEXT NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ix_workspace_artifacts_identity ON workspace_artifacts(workspace_id, artifact_id, version);",
    ];

    public static async Task EnsureCreatedAsync(PlatformDbContext db,
        Microsoft.Extensions.Logging.ILogger? logger = null, CancellationToken ct = default)
    {
        foreach (var statement in Ddl)
        {
            logger?.LogDebug("Ensuring workspace artifact schema: {Statement}", statement);
            await db.Database.ExecuteSqlRawAsync(statement, ct).ConfigureAwait(false);
        }
    }
}
