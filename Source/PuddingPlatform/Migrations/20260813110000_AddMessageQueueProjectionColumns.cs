using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <summary>
    /// Phase 2 消息队列「投影字段契约」：message_deliveries 新增两列
    /// defer_count（int NOT NULL DEFAULT 0，busy 挂起次数）+ execution_state
    /// （nvarchar 可空，从 lastError 解析 busy）。
    /// 同时执行一次性存量脏数据归一脚本：
    /// - attempt_count &gt; 3 截断为 3（历史 attempt_count 高达 248 是旧 busy 空转 bug
    ///   造成的脏数据，真实 retry 上限为 3）；
    /// - status=retrying 且 attempt_count &gt;= 3 且 available_at 已过期 → 重判为 dead_letter；
    /// - defer_count 存量默认 0（保守，无历史数据）；
    /// - execution_state 从 last_error 解析 busy（忽略大小写）。
    /// 注意：运行时存量 SQLite 库由 MessageFabricSchemaBootstrapper 执行等价幂等脚本；
    /// 本迁移覆盖走 EF Migrate() 的干净库/CI 路径。
    /// </summary>
    public partial class AddMessageQueueProjectionColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "defer_count",
                schema: "platform",
                table: "message_deliveries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "execution_state",
                schema: "platform",
                table: "message_deliveries",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            // 一次性归一脚本（存量脏数据清理，幂等）。
            migrationBuilder.Sql(
                """
                UPDATE message_deliveries SET attempt_count = 3 WHERE attempt_count > 3;
                UPDATE message_deliveries SET status = 'dead_letter' WHERE status = 'retrying' AND attempt_count >= 3 AND available_at IS NOT NULL AND available_at <= (strftime('%s','now') * 1000);
                UPDATE message_deliveries SET execution_state = CASE WHEN instr(lower(coalesce(last_error,'')), 'busy') > 0 THEN 'Busy' ELSE NULL END;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "execution_state",
                schema: "platform",
                table: "message_deliveries");

            migrationBuilder.DropColumn(
                name: "defer_count",
                schema: "platform",
                table: "message_deliveries");
        }
    }
}
