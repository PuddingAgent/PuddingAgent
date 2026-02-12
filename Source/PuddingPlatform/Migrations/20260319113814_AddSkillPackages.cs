using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedSkillPackageIdsJson",
                schema: "platform",
                table: "GlobalAgentTemplates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SkillPackages",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SkillPackageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillPackages", x => x.Id);
                });

            migrationBuilder.UpdateData(
                schema: "platform",
                table: "GlobalAgentTemplates",
                keyColumn: "Id",
                keyValue: 1,
                column: "SelectedSkillPackageIdsJson",
                value: "[]");

            migrationBuilder.CreateIndex(
                name: "IX_SkillPackages_SkillPackageId",
                schema: "platform",
                table: "SkillPackages",
                column: "SkillPackageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillPackages",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "SelectedSkillPackageIdsJson",
                schema: "platform",
                table: "GlobalAgentTemplates");
        }
    }
}
