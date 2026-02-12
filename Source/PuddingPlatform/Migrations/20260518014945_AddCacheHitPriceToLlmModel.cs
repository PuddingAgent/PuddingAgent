using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddCacheHitPriceToLlmModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CacheHitPricePer1MTokens",
                schema: "platform",
                table: "LlmModels",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "LlmModels",
                keyColumn: "Id",
                keyValue: 1,
                column: "CacheHitPricePer1MTokens",
                value: 0m);

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "LlmModels",
                keyColumn: "Id",
                keyValue: 2,
                column: "CacheHitPricePer1MTokens",
                value: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheHitPricePer1MTokens",
                schema: "platform",
                table: "LlmModels");
        }
    }
}
