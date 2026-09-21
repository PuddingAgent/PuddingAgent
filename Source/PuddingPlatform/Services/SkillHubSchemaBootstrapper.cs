using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// SKILL Hub 中央技能库 SchemaBootstrapper——为既有 SQLite 数据库幂等补建 4 张 Hub 表与索引。
/// 采用仓内既有 SchemaBootstrapper 模式（TokenUsageSchemaBootstrapper 等）：
/// 仓库的 EF 迁移快照与模型已漂移（dotnet ef migrations add 无法运行，
/// 见 PlatformDbContextModelSnapshot.cs:1529 的 TargetTurnId 崩溃），
/// 生产初始化走 EnsureCreated + SchemaBootstrapper 自愈，本类遵循同一模式。
/// </summary>
public static class SkillHubSchemaBootstrapper
{
    private static readonly (string TableName, string CreateTableSql)[] Tables =
    [
        ("HubSkills",
            """
            CREATE TABLE IF NOT EXISTS "HubSkills" (
                "Id" INTEGER NOT NULL CONSTRAINT "pk_HubSkills" PRIMARY KEY AUTOINCREMENT,
                "SkillId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Summary" TEXT NULL,
                "Description" TEXT NULL,
                "TagsJson" TEXT NOT NULL DEFAULT '[]',
                "KeywordsJson" TEXT NOT NULL DEFAULT '[]',
                "LatestVersion" TEXT NOT NULL DEFAULT '1.0.0',
                "Status" TEXT NOT NULL DEFAULT 'active',
                "Visibility" TEXT NOT NULL DEFAULT 'global',
                "OwnerWorkspaceId" TEXT NULL,
                "SourceAgentId" TEXT NULL,
                "OriginKind" TEXT NOT NULL DEFAULT 'agent-evolved',
                "VersionCount" INTEGER NOT NULL DEFAULT 1,
                "InstallCount" INTEGER NOT NULL DEFAULT 0,
                "PublishCount" INTEGER NOT NULL DEFAULT 1,
                "LatestContentHash" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """),
        ("HubSkillVersions",
            """
            CREATE TABLE IF NOT EXISTS "HubSkillVersions" (
                "Id" INTEGER NOT NULL CONSTRAINT "pk_HubSkillVersions" PRIMARY KEY AUTOINCREMENT,
                "SkillId" TEXT NOT NULL,
                "Version" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "SkillMarkdown" TEXT NOT NULL,
                "ManifestJson" TEXT NOT NULL DEFAULT '{{}}',
                "EvolutionAction" TEXT NOT NULL DEFAULT 'create',
                "ParentVersion" TEXT NULL,
                "RelatedSkillIdsJson" TEXT NULL,
                "PublishedByAgentId" TEXT NULL,
                "PublishedByWorkspaceId" TEXT NULL,
                "PublishNote" TEXT NULL,
                "EvidenceJson" TEXT NULL,
                "ContentBytes" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL
            );
            """),
        ("HubSkillInstalls",
            """
            CREATE TABLE IF NOT EXISTS "HubSkillInstalls" (
                "Id" INTEGER NOT NULL CONSTRAINT "pk_HubSkillInstalls" PRIMARY KEY AUTOINCREMENT,
                "SkillId" TEXT NOT NULL,
                "AgentInstanceId" TEXT NOT NULL,
                "WorkspaceId" TEXT NULL,
                "InstalledVersion" TEXT NOT NULL,
                "ContentHash" TEXT NULL,
                "InstalledBy" TEXT NOT NULL DEFAULT 'agent',
                "InstalledAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """),
        ("HubSkillEvents",
            """
            CREATE TABLE IF NOT EXISTS "HubSkillEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "pk_HubSkillEvents" PRIMARY KEY AUTOINCREMENT,
                "SkillId" TEXT NOT NULL,
                "Version" TEXT NULL,
                "EventType" TEXT NOT NULL,
                "ActorKind" TEXT NOT NULL DEFAULT 'agent',
                "ActorId" TEXT NULL,
                "WorkspaceId" TEXT NULL,
                "PayloadJson" TEXT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """),
    ];

    /// <summary>索引名严格遵循 EF 约定（IX_Table_Cols），与 EnsureCreated 产物一致。</summary>
    private static readonly (string IndexName, string CreateIndexSql)[] Indexes =
    [
        ("IX_HubSkills_SkillId",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_HubSkills_SkillId" ON "HubSkills" ("SkillId");"""),
        ("IX_HubSkillVersions_SkillId_Version",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_HubSkillVersions_SkillId_Version" ON "HubSkillVersions" ("SkillId", "Version");"""),
        ("IX_HubSkillVersions_SkillId",
            """CREATE INDEX IF NOT EXISTS "IX_HubSkillVersions_SkillId" ON "HubSkillVersions" ("SkillId");"""),
        ("IX_HubSkillVersions_ParentVersion",
            """CREATE INDEX IF NOT EXISTS "IX_HubSkillVersions_ParentVersion" ON "HubSkillVersions" ("ParentVersion");"""),
        ("IX_HubSkillInstalls_SkillId_AgentInstanceId",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_HubSkillInstalls_SkillId_AgentInstanceId" ON "HubSkillInstalls" ("SkillId", "AgentInstanceId");"""),
        ("IX_HubSkillInstalls_AgentInstanceId",
            """CREATE INDEX IF NOT EXISTS "IX_HubSkillInstalls_AgentInstanceId" ON "HubSkillInstalls" ("AgentInstanceId");"""),
        ("IX_HubSkillEvents_SkillId_CreatedAt",
            """CREATE INDEX IF NOT EXISTS "IX_HubSkillEvents_SkillId_CreatedAt" ON "HubSkillEvents" ("SkillId", "CreatedAt");"""),
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return; // 非 SQLite（Postgres）走 EF 迁移

        foreach (var (tableName, createTableSql) in Tables)
        {
            if (!await TableExistsAsync(db, tableName, ct))
            {
                await db.Database.ExecuteSqlRawAsync(createTableSql, ct);
                logger?.LogInformation("[SkillHubSchema] Created table {Table}", tableName);
            }
        }

        foreach (var (_, createIndexSql) in Indexes)
        {
            await db.Database.ExecuteSqlRawAsync(createIndexSql, ct);
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbContext db, string tableName, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}
