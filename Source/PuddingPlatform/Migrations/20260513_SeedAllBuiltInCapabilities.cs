using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814

namespace PuddingPlatform.Migrations
{
    public partial class SeedAllBuiltInCapabilities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IDs 1-5 已存在（cap-shell, cap-file-read, cap-file-write, cap-http-fetch）
            // IDs 6-16 新增补齐所有 IAgentSkill 注册的能力
            var epoch = new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

            migrationBuilder.InsertData(
                schema: "platform",
                table: "Capabilities",
                columns: new[] { "Id", "CapabilityId", "CreatedAt", "Description", "IsEnabled", "Name", "RequiresFileWrite", "RequiresNetworkAccess", "RequiresShellExecution", "SortOrder", "ToolDescription", "ToolName", "ToolParametersJson", "UpdatedAt" },
                values: new object[,]
                {
                    // ── 默认能力（只读 / 记忆 / 子代理 / 内部）──
                    {
                        6, "cap-spawn-sub-agent", epoch,
                        "派生子代理执行独立任务。子代理拥有独立上下文窗口。",
                        true, "派生子代理", false, false, false, 60,
                        "Spawn a sub-agent to execute a task independently with its own context window.",
                        "spawn_sub_agent",
                        "{\"type\":\"object\",\"properties\":{\"task\":{\"type\":\"string\",\"description\":\"子代理要执行的任务描述\"},\"agent_template\":{\"type\":\"string\",\"description\":\"Agent 模板ID\"},\"sync\":{\"type\":\"boolean\",\"description\":\"同步等待(默认)或异步执行\"}},\"required\":[\"task\"]}",
                        epoch
                    },
                    {
                        7, "cap-memory-library", epoch,
                        "查询记忆图书馆，召回与当前上下文相关的记忆片段。",
                        true, "记忆图书馆", false, false, false, 70,
                        "Query the memory library to recall relevant past context.",
                        "search_memory",
                        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"自然语言查询\"},\"book\":{\"type\":\"string\",\"description\":\"可选：限定记忆本名称\"}},\"required\":[\"query\"]}",
                        epoch
                    },
                    {
                        8, "cap-save-memory", epoch,
                        "将当前对话中的重要信息保存到记忆图书馆供未来召回。",
                        true, "保存记忆", false, false, false, 80,
                        "Save important information from the current conversation to the memory library.",
                        "save_memory",
                        "{\"type\":\"object\",\"properties\":{\"memory_content\":{\"type\":\"string\",\"description\":\"要保存的记忆内容\"},\"memory_type\":{\"type\":\"string\",\"description\":\"记忆类型：short / long\"}},\"required\":[\"memory_content\"]}",
                        epoch
                    },
                    {
                        9, "cap-manage-memory", epoch,
                        "管理记忆条目：浏览、搜索、标记重要度、清理过期记忆。",
                        true, "记忆管理", false, false, false, 85,
                        "Manage memory entries: browse, search, mark importance, clean stale memories.",
                        "manage_memory",
                        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"操作: list/search/mark/clean\"},\"keyword\":{\"type\":\"string\",\"description\":\"搜索或标记关键词\"}},\"required\":[\"action\"]}",
                        epoch
                    },
                    {
                        10, "cap-grep-memory", epoch,
                        "全文搜索记忆库，快速定位包含关键词的记忆条目。",
                        true, "记忆搜索", false, false, false, 90,
                        "Full-text search the memory store for entries containing specific keywords.",
                        "grep_memory",
                        "{\"type\":\"object\",\"properties\":{\"keyword\":{\"type\":\"string\",\"description\":\"搜索关键词\"}},\"required\":[\"keyword\"]}",
                        epoch
                    },
                    {
                        11, "cap-query-sessions", epoch,
                        "查询历史会话的完整对话记录。支持分页和时间游标。",
                        true, "查询会话记录", false, false, false, 95,
                        "Query complete conversation records from historical sessions with pagination.",
                        "query_sessions",
                        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"messages 或 recent\"},\"session_id\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"action\"]}",
                        epoch
                    },
                    {
                        12, "cap-file-search", epoch,
                        "通过指定文件搜索服务商按名称搜索文件。支持 Everything 和 BuiltInRecursiveFileSearch；搜索时必须传 provider，可用 action=list 查看服务商状态。",
                        true, "文件搜索", false, false, false, 100,
                        "Search file names through an explicit file search provider. Supported providers: Everything, BuiltInRecursiveFileSearch. Use action=list to inspect provider availability.",
                        "file_search",
                        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"Action to perform: list or search. Default: search.\"},\"provider\":{\"type\":\"string\",\"description\":\"File search provider id. Required for search. Supported providers: Everything, BuiltInRecursiveFileSearch.\"},\"pattern\":{\"type\":\"string\",\"description\":\"File name text or glob pattern. Default: *\"},\"directory\":{\"type\":\"string\",\"description\":\"Root directory to search. Everything treats null or empty as full-disk search. BuiltInRecursiveFileSearch requires an existing directory.\"},\"recursive\":{\"type\":\"boolean\",\"description\":\"Search subdirectories. Default: true.\"},\"max_results\":{\"type\":\"integer\",\"description\":\"Maximum result count, 1-500. Default: 50.\"}},\"required\":[]}",
                        epoch
                    },
                    {
                        13, "cap-search-codebase", epoch,
                        "在代码库中语义搜索相关代码片段、函数和类定义。",
                        true, "代码库搜索", false, false, false, 105,
                        "Semantically search the codebase for relevant code snippets, functions, and class definitions.",
                        "search_grep",
                        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"代码语义搜索查询\"},\"language\":{\"type\":\"string\"}},\"required\":[\"query\"]}",
                        epoch
                    },
                    {
                        14, "cap-task-manager", epoch,
                        "管理 Agent 内部任务列表：创建、更新状态、列出、删除。",
                        true, "任务管理", false, false, false, 110,
                        "Manage the agent's internal task list: create, update status, list, delete.",
                        "manage_tasks",
                        "{\"type\":\"object\",\"properties\":{\"operation\":{\"type\":\"string\",\"description\":\"操作: create/update_status/list/delete\"},\"title\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"}},\"required\":[\"operation\"]}",
                        epoch
                    },
                    {
                        16, "cap-file-patch", epoch,
                        "对宿主工作区中的文本文件执行局部补丁、批量替换或正则替换。",
                        true, "文件补丁", true, false, false, 120,
                        "Patch text files in the host workspace. Supports single/batch and regex replacements.",
                        "file_patch",
                        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"单文件路径\"},\"operations\":{\"type\":\"array\",\"description\":\"单文件补丁操作\"},\"patches\":{\"type\":\"array\",\"description\":\"批量补丁列表\"}},\"required\":[]}",
                        epoch
                    },
                    {
                        15, "cap-event-subscribe", epoch,
                        "管理 Agent 事件订阅：订阅/取消订阅事件类型，列出当前订阅。",
                        true, "事件订阅管理", false, false, false, 115,
                        "Manage agent event subscriptions: subscribe/unsubscribe event types, list active subscriptions.",
                        "event_subscribe",
                        "{\"type\":\"object\",\"properties\":{\"operation\":{\"type\":\"string\",\"description\":\"subscribe/unsubscribe/list\"},\"event_types\":{\"type\":\"string\",\"description\":\"逗号分隔的事件类型\"}},\"required\":[\"operation\"]}",
                        epoch
                    },
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            for (int id = 6; id <= 16; id++)
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
