using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PuddingPlatform.Migrations;

/// <summary>新增 KeyVault 密钥保管箱表。</summary>
public partial class AddKeyVault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "KeyVaults",
            schema: "platform",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KeyVaultId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                EncryptedValue = table.Column<string>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Tags = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KeyVaults", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KeyVaults_KeyVaultId",
            schema: "platform",
            table: "KeyVaults",
            column: "KeyVaultId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_KeyVaults_Name",
            schema: "platform",
            table: "KeyVaults",
            column: "Name",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "KeyVaults",
            schema: "platform");
    }
}
