using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PuddingPlatform.Data;

#nullable disable

namespace PuddingPlatform.Migrations;

/// <summary>
/// 为 Agent 模板新增 Persona 分层字段，并为 Workspace 新增 UserProfile。
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260505103000_AddAgentPersonaAndWorkspaceUserProfile")]
public partial class AddAgentPersonaAndWorkspaceUserProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PersonaPrompt",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ToolsDescription",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BootstrapTemplate",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AvatarEmoji",
            schema: "platform",
            table: "GlobalAgentTemplates",
            type: "TEXT",
            maxLength: 8,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PersonaPrompt",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ToolsDescription",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BootstrapTemplate",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AvatarEmoji",
            schema: "platform",
            table: "WorkspaceAgentTemplates",
            type: "TEXT",
            maxLength: 8,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "UserProfile",
            schema: "platform",
            table: "Workspaces",
            type: "TEXT",
            maxLength: 8000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PersonaPrompt",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "ToolsDescription",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "BootstrapTemplate",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "AvatarEmoji",
            schema: "platform",
            table: "GlobalAgentTemplates");

        migrationBuilder.DropColumn(
            name: "PersonaPrompt",
            schema: "platform",
            table: "WorkspaceAgentTemplates");

        migrationBuilder.DropColumn(
            name: "ToolsDescription",
            schema: "platform",
            table: "WorkspaceAgentTemplates");

        migrationBuilder.DropColumn(
            name: "BootstrapTemplate",
            schema: "platform",
            table: "WorkspaceAgentTemplates");

        migrationBuilder.DropColumn(
            name: "AvatarEmoji",
            schema: "platform",
            table: "WorkspaceAgentTemplates");

        migrationBuilder.DropColumn(
            name: "UserProfile",
            schema: "platform",
            table: "Workspaces");
    }
}
