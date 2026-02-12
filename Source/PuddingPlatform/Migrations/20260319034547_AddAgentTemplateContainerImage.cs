using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentTemplateContainerImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContainerImage",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContainerImage",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "GlobalAgentTemplates",
                keyColumn: "Id",
                keyValue: 1,
                column: "ContainerImage",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContainerImage",
                schema: "platform",
                table: "WorkspaceAgentTemplates");

            migrationBuilder.DropColumn(
                name: "ContainerImage",
                schema: "platform",
                table: "GlobalAgentTemplates");
        }
    }
}
