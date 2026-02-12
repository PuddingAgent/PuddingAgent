using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PuddingPlatform.Data;

#nullable disable

namespace PuddingPlatform.Migrations;

/// <summary>
/// 潜意识模型配置改为引用 LLM 资源池 Provider + Model，不再在模板表保存 Endpoint / ApiKey。
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260523093000_UseLlmPoolForAgentTemplateMemoryModel")]
public partial class UseLlmPoolForAgentTemplateMemoryModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        DropColumnCompat(migrationBuilder, "GlobalAgentTemplates", "MemoryLlmEndpoint");
        DropColumnCompat(migrationBuilder, "GlobalAgentTemplates", "MemoryLlmApiKey");
        DropColumnCompat(migrationBuilder, "WorkspaceAgentTemplates", "MemoryLlmEndpoint");
        DropColumnCompat(migrationBuilder, "WorkspaceAgentTemplates", "MemoryLlmApiKey");

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmProviderId",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmProviderId",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        DropColumnCompat(migrationBuilder, "GlobalAgentTemplates", "MemoryLlmProviderId");
        DropColumnCompat(migrationBuilder, "WorkspaceAgentTemplates", "MemoryLlmProviderId");

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmEndpoint",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmApiKey",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmEndpoint",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmApiKey",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 512,
            nullable: true);
    }

    private static void DropColumnCompat(MigrationBuilder migrationBuilder, string table, string column)
    {
        if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            migrationBuilder.Sql($@"ALTER TABLE ""{table}"" DROP COLUMN ""{column}"";");
            return;
        }

        migrationBuilder.DropColumn(
            name: column,
            schema: "platform",
            table: table);
    }
}
