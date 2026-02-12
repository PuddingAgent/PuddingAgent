using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddMoreBuiltInCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "platform",
                table: "Capabilities",
                columns: new[] { "Id", "CapabilityId", "CreatedAt", "Description", "IsEnabled", "Name", "RequiresFileWrite", "RequiresNetworkAccess", "RequiresShellExecution", "SortOrder", "ToolDescription", "ToolName", "ToolParametersJson", "UpdatedAt" },
                values: new object[,]
                {
                    { 3, "cap-file-read", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 读取宿主工作区内的文件内容。", true, "读取文件", false, false, false, 30, "Read a UTF-8 text file from the host workspace.", "file_read", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Absolute or relative file path inside the host workspace\"},\"max_chars\":{\"type\":\"integer\",\"description\":\"Maximum characters to return\"}},\"required\":[\"path\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 4, "cap-file-write", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 在宿主工作区内创建或覆写文件。", true, "写入文件", true, false, false, 40, "Create or overwrite a UTF-8 text file in the host workspace.", "file_write", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"File path to write\"},\"content\":{\"type\":\"string\",\"description\":\"Text content to write to the file\"}},\"required\":[\"path\",\"content\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 5, "cap-http-fetch", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 发起 HTTP GET/POST 请求，访问外部 API 或网页。", true, "HTTP 请求", false, true, false, 50, "Make an HTTP GET or POST request to a URL and return the response body.", "http_fetch", "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\",\"description\":\"The full HTTP/HTTPS URL to request\"},\"method\":{\"type\":\"string\",\"description\":\"HTTP method: GET or POST (default: GET)\"},\"body\":{\"type\":\"string\",\"description\":\"Request body for POST requests (optional)\"}},\"required\":[\"url\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "platform",
                table: "Capabilities",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                schema: "platform",
                table: "Capabilities",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                schema: "platform",
                table: "Capabilities",
                keyColumn: "Id",
                keyValue: 5);
        }
    }
}
