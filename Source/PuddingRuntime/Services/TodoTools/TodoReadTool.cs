using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TodoTools;

/// <summary>
/// todo_read — 读取某作用域（goal/task/session）的拆解 TODO 表与汇总（设计 2026-09-16 §4，TD-1）。
/// </summary>
[Tool(
    id: "todo_read",
    name: "读取拆解 TODO",
    description: "读取某作用域（goal/task/session）的拆解 TODO 表与汇总（总数/各状态计数/当前 in_progress/受阻项），面板与 Agent 同一视图。【何时用】写入前先读最新 revision（CAS 用）；检查拆解进度与受阻项；面板打开时拉取最新。【怎么用】scope_kind=goal|task|session + scope_id 定位列表；items 按 order_index 排序返回，含每项 status/note/evidence_ref/blocked_reason/时间戳；列表不存在返回 found=false（不是错误，表示该作用域尚未写拆解）。【坑】拆解进度是 Agent 自述，不代表目标达成；验收进度以平台 verifier 为准。Read the breakdown TODO list and its summary for a scope; items are ordered by order_index; returns found=false when the list does not exist yet (not an error).",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定同款：轻量元数据工具（用户原则：仅直接损坏/泄露用户数据需门禁）
public sealed class TodoReadTool(ITodoStore store)
    : PuddingToolBase<TodoReadArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TodoReadArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        try
        {
            var result = await store.ReadAsync(new TodoReadQuery
            {
                ScopeKind = args.ScopeKind,
                ScopeId = args.ScopeId,
            }, ct);

            return ToolExecutionResult.Ok(result is null
                ? TodoToolJson.Serialize(new TodoReadNotFoundResult
                {
                    ScopeKind = args.ScopeKind,
                    ScopeId = args.ScopeId,
                })
                : TodoToolJson.Serialize(result));
        }
        catch (TodoStoreException ex)
        {
            return ToolExecutionResult.Fail(TodoToolErrors.BuildErrorJson(ex));
        }
    }
}
