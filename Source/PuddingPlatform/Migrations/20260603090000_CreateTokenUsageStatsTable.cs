using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PuddingPlatform.Data;

#nullable disable

namespace PuddingPlatform.Migrations;

/// <summary>
/// 补齐 ADR-018 月度 Token 用量汇总表的实际迁移。
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260603090000_CreateTokenUsageStatsTable")]
public partial class CreateTokenUsageStatsTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "TokenUsageStats" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_TokenUsageStats" PRIMARY KEY AUTOINCREMENT,
                    "ProviderId" TEXT NOT NULL,
                    "ModelId" TEXT NOT NULL,
                    "YearMonth" TEXT NOT NULL,
                    "PromptTokens" INTEGER NOT NULL,
                    "CompletionTokens" INTEGER NOT NULL,
                    "CacheHitTokens" INTEGER NOT NULL,
                    "CacheMissTokens" INTEGER NOT NULL,
                    "RequestCount" INTEGER NOT NULL,
                    "TotalCost" decimal(18,6) NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_TokenUsageStats_YearMonth_ProviderId_ModelId"
                ON "TokenUsageStats" ("YearMonth", "ProviderId", "ModelId");
                """);
            return;
        }

        migrationBuilder.CreateTable(
            name: "TokenUsageStats",
            schema: "platform",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                YearMonth = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                CompletionTokens = table.Column<long>(type: "INTEGER", nullable: false),
                CacheHitTokens = table.Column<long>(type: "INTEGER", nullable: false),
                CacheMissTokens = table.Column<long>(type: "INTEGER", nullable: false),
                RequestCount = table.Column<long>(type: "INTEGER", nullable: false),
                TotalCost = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TokenUsageStats", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TokenUsageStats_YearMonth_ProviderId_ModelId",
            schema: "platform",
            table: "TokenUsageStats",
            columns: ["YearMonth", "ProviderId", "ModelId"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TokenUsageStats",
            schema: "platform");
    }
}
