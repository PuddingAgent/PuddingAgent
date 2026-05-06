using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PuddingPlatform.Data;

#nullable disable

namespace PuddingPlatform.Migrations;

/// <summary>
/// 为全局/工作区 Agent 模板新增记忆专用 LLM 配置字段。
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260505143000_AddAgentTemplateMemoryLlmConfig")]
public partial class AddAgentTemplateMemoryLlmConfig : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
            name: "MemoryLlmModelId",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 128,
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

        migrationBuilder.AddColumn<string>(
            name: "MemoryLlmModelId",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MemoryLlmEndpoint",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "MemoryLlmApiKey",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "MemoryLlmModelId",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "MemoryLlmEndpoint",
            schema: "platform",
            table: "WorkspaceAgentTemplates");

        migrationBuilder.DropColumn(
            name: "MemoryLlmApiKey",
            schema: "platform",
            table: "WorkspaceAgentTemplates");

        migrationBuilder.DropColumn(
            name: "MemoryLlmModelId",
            schema: "platform",
            table: "WorkspaceAgentTemplates");
    }
}
