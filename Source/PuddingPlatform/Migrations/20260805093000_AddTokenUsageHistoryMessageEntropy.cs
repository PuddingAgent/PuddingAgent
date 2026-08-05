using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <summary>
    /// 为 TokenUsageEvents 表新增 HistoryMessageEntropy（历史消息层 gzip 熵探针）列。
    /// 可空 REAL，向后兼容旧记录。生产库运行时由 TokenUsageSchemaBootstrapper 幂等自愈补齐。
    /// </summary>
    public partial class AddTokenUsageHistoryMessageEntropy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double?>(
                name: "HistoryMessageEntropy",
                schema: "platform",
                table: "TokenUsageEvents",
                type: "REAL",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HistoryMessageEntropy",
                schema: "platform",
                table: "TokenUsageEvents");
        }
    }
}
