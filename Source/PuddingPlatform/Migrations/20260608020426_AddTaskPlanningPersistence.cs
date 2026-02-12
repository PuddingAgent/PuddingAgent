using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskPlanningPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_plan_runs",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    plan_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    workspace_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    root_session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    leader_agent_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    objective = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Draft"),
                    max_delegation_depth = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 2),
                    default_allow_sub_delegation = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    allow_agent_creation_by_leader = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    max_active_task_nodes_per_plan = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 50),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    result_summary = table.Column<string>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    trace_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    correlation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_plan_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_plan_runs_plan_id",
                schema: "platform",
                table: "task_plan_runs",
                column: "plan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_plan_runs_workspace_id_status_updated_at",
                schema: "platform",
                table: "task_plan_runs",
                columns: new[] { "workspace_id", "status", "updated_at" });

            migrationBuilder.CreateTable(
                name: "task_nodes",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    task_node_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    plan_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    parent_task_node_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    objective = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    input_context_summary = table.Column<string>(type: "TEXT", nullable: true),
                    expected_output_contract = table.Column<string>(type: "TEXT", nullable: true),
                    assigned_to_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Unassigned"),
                    assigned_to_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    assigned_template_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_by_agent_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Draft"),
                    allow_sub_delegation = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    allow_agent_creation = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    result_summary = table.Column<string>(type: "TEXT", nullable: true),
                    result_artifact_ref = table.Column<string>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    superseded_by_task_node_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_nodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_nodes_task_node_id",
                schema: "platform",
                table: "task_nodes",
                column: "task_node_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_nodes_plan_id_parent_task_node_id_status",
                schema: "platform",
                table: "task_nodes",
                columns: new[] { "plan_id", "parent_task_node_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_task_nodes_plan_id_depth_status",
                schema: "platform",
                table: "task_nodes",
                columns: new[] { "plan_id", "depth", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_nodes",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "task_plan_runs",
                schema: "platform");
        }
    }
}
