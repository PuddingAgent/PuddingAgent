using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentProfileAndChatMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                schema: "platform",
                table: "WorkspaceAgents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "platform",
                table: "WorkspaceAgents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ThinkingJson = table.Column<string>(type: "text", nullable: true),
                    UsageJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SessionId",
                schema: "platform",
                table: "ChatMessages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SessionId_CreatedAt",
                schema: "platform",
                table: "ChatMessages",
                columns: new[] { "SessionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                schema: "platform",
                table: "WorkspaceAgents");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "platform",
                table: "WorkspaceAgents");
        }
    }
}
