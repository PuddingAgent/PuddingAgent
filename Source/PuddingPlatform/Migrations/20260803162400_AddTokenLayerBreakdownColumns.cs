using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <summary>
    /// 为 TokenUsageEvents 表新增 4 个上下文分层 token 列。
    /// MessageTokens / ToolDefinitionTokens / SystemMessageTokens / HistoryMessageTokens。
    /// 全部可为 null（向后兼容既有无分层数据的旧记录）。
    /// </summary>
    public partial class AddTokenLayerBreakdownColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int?>(
                name: "MessageTokens",
                schema: "platform",
                table: "TokenUsageEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "ToolDefinitionTokens",
                schema: "platform",
                table: "TokenUsageEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "SystemMessageTokens",
                schema: "platform",
                table: "TokenUsageEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "HistoryMessageTokens",
                schema: "platform",
                table: "TokenUsageEvents",
                type: "INTEGER",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HistoryMessageTokens",
                schema: "platform",
                table: "TokenUsageEvents");

            migrationBuilder.DropColumn(
                name: "SystemMessageTokens",
                schema: "platform",
                table: "TokenUsageEvents");

            migrationBuilder.DropColumn(
                name: "ToolDefinitionTokens",
                schema: "platform",
                table: "TokenUsageEvents");

            migrationBuilder.DropColumn(
                name: "MessageTokens",
                schema: "platform",
                table: "TokenUsageEvents");
        }
    }
}
