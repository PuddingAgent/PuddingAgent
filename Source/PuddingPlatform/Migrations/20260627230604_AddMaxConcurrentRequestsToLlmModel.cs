using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <summary>
    /// 为 LlmModels 表新增 MaxConcurrentRequests 列。
    /// 允许为 null（null=使用 Provider 默认值 50），支持模型级别的独立并发限流。
    /// </summary>
    public partial class AddMaxConcurrentRequestsToLlmModel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int?>(
                name: "MaxConcurrentRequests",
                schema: "platform",
                table: "LlmModels",
                type: "INTEGER",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxConcurrentRequests",
                schema: "platform",
                table: "LlmModels");
        }
    }
}
