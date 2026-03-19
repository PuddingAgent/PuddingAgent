using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingController.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ctrl");

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                schema: "ctrl",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    MessageId = table.Column<string>(type: "text", nullable: true),
                    WorkspaceId = table.Column<string>(type: "text", nullable: true),
                    AgentTemplateId = table.Column<string>(type: "text", nullable: true),
                    ApprovalId = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "RouteDecisions",
                schema: "ctrl",
                columns: table => new
                {
                    RouteDecisionId = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: true),
                    AgentTemplateId = table.Column<string>(type: "text", nullable: true),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    IsSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteDecisions", x => x.RouteDecisionId);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceDefinitions",
                schema: "ctrl",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsFrozen = table.Column<bool>(type: "boolean", nullable: false),
                    ChannelBindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    AgentTemplateIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    AuditAgentTemplateIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PermissionPolicyJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExtrasJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceDefinitions", x => x.WorkspaceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SessionId",
                schema: "ctrl",
                table: "AuditEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Timestamp",
                schema: "ctrl",
                table: "AuditEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_WorkspaceId",
                schema: "ctrl",
                table: "AuditEvents",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_RouteDecisions_MessageId",
                schema: "ctrl",
                table: "RouteDecisions",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_RouteDecisions_Timestamp",
                schema: "ctrl",
                table: "RouteDecisions",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_RouteDecisions_WorkspaceId",
                schema: "ctrl",
                table: "RouteDecisions",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents",
                schema: "ctrl");

            migrationBuilder.DropTable(
                name: "RouteDecisions",
                schema: "ctrl");

            migrationBuilder.DropTable(
                name: "WorkspaceDefinitions",
                schema: "ctrl");
        }
    }
}
