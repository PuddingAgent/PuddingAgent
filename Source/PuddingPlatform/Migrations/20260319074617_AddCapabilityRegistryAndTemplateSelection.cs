using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddCapabilityRegistryAndTemplateSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedCapabilityIdsJson",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SelectedCapabilityIdsJson",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "Capabilities",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CapabilityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ToolDescription = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ToolParametersJson = table.Column<string>(type: "text", nullable: true),
                    RequiresShellExecution = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresFileWrite = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresNetworkAccess = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Capabilities", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "Capabilities",
                columns: new[] { "Id", "CapabilityId", "CreatedAt", "Description", "IsEnabled", "Name", "RequiresFileWrite", "RequiresNetworkAccess", "RequiresShellExecution", "SortOrder", "ToolDescription", "ToolName", "ToolParametersJson", "UpdatedAt" },
                values: new object[] { 1, "cap-bash", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 在隔离容器中执行 bash 命令。", true, "Bash 命令执行", false, false, true, 10, "Execute a bash shell command inside the agent sandbox container.", "bash", "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"Shell command to execute\"}},\"required\":[\"command\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "GlobalAgentTemplates",
                keyColumn: "Id",
                keyValue: 1,
                column: "SelectedCapabilityIdsJson",
                value: "[]");

            migrationBuilder.CreateIndex(
                name: "IX_Capabilities_CapabilityId",
                schema: "platform",
                table: "Capabilities",
                column: "CapabilityId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Capabilities",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "SelectedCapabilityIdsJson",
                schema: "platform",
                table: "WorkspaceAgentTemplates");

            migrationBuilder.DropColumn(
                name: "SelectedCapabilityIdsJson",
                schema: "platform",
                table: "GlobalAgentTemplates");
        }
    }
}
