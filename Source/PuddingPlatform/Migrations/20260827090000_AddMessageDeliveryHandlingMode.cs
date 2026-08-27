using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <summary>
    /// Separates executable Agent requests from passive notifications. Existing
    /// inform/report_result/agent_reply rows are backfilled to notify so restart
    /// recovery can drain them without waking the model.
    /// </summary>
    public partial class AddMessageDeliveryHandlingMode : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "handling_mode",
                schema: "platform",
                table: "message_deliveries",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "execute");

            migrationBuilder.Sql(
                """
                UPDATE message_deliveries
                SET handling_mode = 'notify'
                WHERE message_id IN (
                    SELECT message_id
                    FROM room_messages
                    WHERE replace(lower(coalesce(metadata_json, '')), ' ', '') LIKE '%"intent":"agent_reply"%'
                       OR replace(lower(coalesce(metadata_json, '')), ' ', '') LIKE '%"intent":"report_result"%'
                       OR (
                            replace(lower(coalesce(metadata_json, '')), ' ', '') LIKE '%"intent":"inform"%'
                            AND replace(lower(coalesce(metadata_json, '')), ' ', '') NOT LIKE '%"requires_response":"true"%'
                       )
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_message_deliveries_workspace_id_target_kind_target_id_handling_mode_status",
                schema: "platform",
                table: "message_deliveries",
                columns: new[]
                {
                    "workspace_id",
                    "target_kind",
                    "target_id",
                    "handling_mode",
                    "status",
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_message_deliveries_workspace_id_target_kind_target_id_handling_mode_status",
                schema: "platform",
                table: "message_deliveries");

            migrationBuilder.DropColumn(
                name: "handling_mode",
                schema: "platform",
                table: "message_deliveries");
        }
    }
}
