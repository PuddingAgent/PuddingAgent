using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814

namespace PuddingPlatform.Migrations
{
    public partial class SeedMissingToolCapabilities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var epoch = new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

            migrationBuilder.InsertData(
                schema: "platform",
                table: "Capabilities",
                columns: new[] { "Id", "CapabilityId", "CreatedAt", "Description", "IsEnabled", "Name", "RequiresFileWrite", "RequiresNetworkAccess", "RequiresShellExecution", "SortOrder", "ToolDescription", "ToolName", "ToolParametersJson", "UpdatedAt" },
                values: new object[,]
                {
                    {
                        17, "cap-query-session-logs", epoch,
                        "查询指定会话的日志片段、工具调用和分页记录。",
                        true, "查询会话日志", false, false, false, 96,
                        "Query session logs, tool calls, and paginated transcript fragments.",
                        "query_session_logs",
                        "{\"type\":\"object\",\"properties\":{\"session_id\":{\"type\":\"string\",\"description\":\"Session id to query.\"},\"page\":{\"type\":\"integer\",\"description\":\"Page number, starting from 1.\"},\"page_size\":{\"type\":\"integer\",\"description\":\"Page size.\"},\"diagnostic\":{\"type\":\"boolean\",\"description\":\"Include diagnostic metadata.\"}},\"required\":[\"session_id\"]}",
                        epoch
                    },
                    {
                        18, "cap-send-message", epoch,
                        "向用户、代理或房间发送消息。",
                        true, "发送消息", false, false, false, 125,
                        "Send a message to users, agents, rooms, or broadcasts.",
                        "send_message",
                        "{\"type\":\"object\",\"properties\":{\"to\":{\"type\":\"string\",\"description\":\"Target address list. Examples: user:owner, agent:assistant, room:default, @all.\"},\"content\":{\"type\":\"string\",\"description\":\"Message content.\"},\"audience\":{\"type\":\"string\",\"description\":\"Optional audience: direct/broadcast/room.\"},\"visibility\":{\"type\":\"string\",\"description\":\"Optional visibility: private/public/system.\"},\"room_id\":{\"type\":\"string\",\"description\":\"Optional room id.\"},\"priority\":{\"type\":\"integer\",\"description\":\"Optional priority.\"},\"reply_to_message_id\":{\"type\":\"string\",\"description\":\"Optional replied message id.\"}},\"required\":[\"to\",\"content\"]}",
                        epoch
                    },
                    {
                        19, "cap-receive-messages", epoch,
                        "读取当前代理、用户或房间的消息投递记录。",
                        true, "接收消息", false, false, false, 126,
                        "Receive message deliveries for an endpoint or room.",
                        "receive_messages",
                        "{\"type\":\"object\",\"properties\":{\"endpoint_id\":{\"type\":\"string\",\"description\":\"Optional endpoint id. Defaults to current agent instance.\"},\"endpoint_kind\":{\"type\":\"string\",\"description\":\"Optional endpoint kind. Defaults to agent.\"},\"room_id\":{\"type\":\"string\",\"description\":\"Optional room id filter.\"},\"limit\":{\"type\":\"integer\",\"description\":\"Maximum messages, 1-100.\"},\"include_delivered\":{\"type\":\"boolean\",\"description\":\"Include already delivered messages.\"},\"ack\":{\"type\":\"boolean\",\"description\":\"Acknowledge returned deliveries.\"}},\"required\":[]}",
                        epoch
                    },
                    {
                        20, "cap-query-sub-agents", epoch,
                        "查询子代理列表、状态、统计和最近活动。",
                        true, "查询子代理", false, false, false, 130,
                        "Query sub-agent list, status, statistics, and recent activity.",
                        "query_sub_agents",
                        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"Action: list/stats/status/grep/recent/running.\"},\"sub_agent_id\":{\"type\":\"string\",\"description\":\"Sub-agent id for status.\"},\"keyword\":{\"type\":\"string\",\"description\":\"Keyword for grep.\"},\"days\":{\"type\":\"integer\",\"description\":\"Day count for recent.\"}},\"required\":[\"action\"]}",
                        epoch
                    },
                    {
                        21, "cap-terminal-execute", epoch,
                        "执行长期终端命令并订阅进程输出流。",
                        true, "终端执行", false, false, true, 140,
                        "Execute a terminal command and stream process output.",
                        "terminal_execute",
                        "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"Command line to execute.\"},\"cwd\":{\"type\":\"string\",\"description\":\"Working directory.\"}},\"required\":[\"command\"]}",
                        epoch
                    },
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            for (int id = 17; id <= 21; id++)
            {
                migrationBuilder.DeleteData(
                    schema: "platform",
                    table: "Capabilities",
                    keyColumn: "Id",
                    keyValue: id);
            }
        }
    }
}
