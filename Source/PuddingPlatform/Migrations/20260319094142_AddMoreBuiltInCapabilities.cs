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
                    { 2, "cap-python", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 在隔离容器中执行 Python 3 代码。", true, "Python 代码执行", false, false, true, 20, "Execute Python 3 code inside the agent sandbox container.", "python", "{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"description\":\"Python 3 code to execute\"}},\"required\":[\"code\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 3, "cap-read-file", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 读取沙箱容器内的文件内容。", true, "读取文件", false, false, true, 30, "Read the content of a file from the agent's container filesystem.", "read_file", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Absolute or relative file path inside the container\"}},\"required\":[\"path\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 4, "cap-write-file", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "允许 Agent 在沙箱容器内创建或覆写文件。", true, "写入文件", true, false, true, 40, "Create or overwrite a file in the agent's container filesystem.", "write_file", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"File path to write\"},\"content\":{\"type\":\"string\",\"description\":\"Text content to write to the file\"}},\"required\":[\"path\",\"content\"]}", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
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
                keyValue: 2);

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
