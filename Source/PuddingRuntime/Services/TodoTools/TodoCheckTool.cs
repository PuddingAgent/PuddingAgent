using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TodoTools;

/// <summary>
/// todo_check — 勾选拆解 TODO 单项状态（设计 2026-09-16 §4，TD-1）。
/// </summary>
[Tool(
    id: "todo_check",
    name: "勾选拆解 TODO",
    description: "勾选拆解 TODO 单项状态（completed / in_progress / blocked+reason），单项更新比全量替换更适合收尾动作。【何时用】完成一项（建议带 evidence_ref）；开始下一项；报告某项受阻（必填 blocked_reason）。【怎么用】scope_kind=goal|task|session + scope_id + slug 定位单项；status=completed|in_progress|blocked|pending；服务端约束：同一时刻最多 1 项 in_progress（冲突返回 todo.multiple_in_progress）、blocked 必填 blocked_reason；expected_revision 传 todo_read/todo_write 返回的最新 revision（CAS 校验，不符返回 todo.version_conflict）；返回新 revision、更新后的单项与汇总。【坑】勾选完成只更新自述拆解表，不推进 Task 状态、不触发验收——任务收口仍要走 task_update。Check off a single TODO item (completed/in_progress/blocked+reason) with server-side CAS and constraint enforcement; does NOT advance the Task board or trigger acceptance.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定同款：轻量元数据工具（用户原则：仅直接损坏/泄露用户数据需门禁）
public sealed class TodoCheckTool(ITodoStore store)
    : PuddingToolBase<TodoCheckArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TodoCheckArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        try
        {
            var result = await store.CheckAsync(new TodoCheckRequest
            {
                ScopeKind = args.ScopeKind,
                ScopeId = args.ScopeId,
                Slug = args.Slug,
                Status = args.Status,
                BlockedReason = args.BlockedReason,
                EvidenceRef = args.EvidenceRef,
                Note = args.Note,
                ExpectedRevision = args.ExpectedRevision,
            }, ct);

            return ToolExecutionResult.Ok(TodoToolJson.Serialize(result));
        }
        catch (TodoStoreException ex)
        {
            return ToolExecutionResult.Fail(TodoToolErrors.BuildErrorJson(ex));
        }
    }
}
