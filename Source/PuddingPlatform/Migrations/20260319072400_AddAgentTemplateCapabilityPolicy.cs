using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentTemplateCapabilityPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowFileWrite",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowNetworkAccess",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowShellExecution",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AllowedToolNamesJson",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AllowFileWrite",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowNetworkAccess",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowShellExecution",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AllowedToolNamesJson",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "GlobalAgentTemplates",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AllowFileWrite", "AllowNetworkAccess", "AllowShellExecution", "AllowedToolNamesJson" },
                values: new object[] { false, false, false, "[]" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowFileWrite",
                schema: "platform",
                table: "WorkspaceAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowNetworkAccess",
                schema: "platform",
                table: "WorkspaceAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowShellExecution",
                schema: "platform",
                table: "WorkspaceAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowedToolNamesJson",
                schema: "platform",
                table: "WorkspaceAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowFileWrite",
                schema: "platform",
                table: "GlobalAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowNetworkAccess",
                schema: "platform",
                table: "GlobalAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowShellExecution",
                schema: "platform",
                table: "GlobalAgentTemplates");

            migrationBuilder.DropColumn(
                name: "AllowedToolNamesJson",
                schema: "platform",
                table: "GlobalAgentTemplates");
        }
    }
}
