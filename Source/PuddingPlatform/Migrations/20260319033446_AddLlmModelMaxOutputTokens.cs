using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmModelMaxOutputTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxOutputTokens",
                schema: "platform",
                table: "LlmModels",
                type: "integer",
                nullable: false,
                defaultValue: 2048);

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "LlmModels",
                keyColumn: "Id",
                keyValue: 1,
                column: "MaxOutputTokens",
                value: 4096);

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "LlmModels",
                keyColumn: "Id",
                keyValue: 2,
                column: "MaxOutputTokens",
                value: 4096);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxOutputTokens",
                schema: "platform",
                table: "LlmModels");
        }
    }
}
