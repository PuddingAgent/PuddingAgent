using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TodoTools;

/// <summary>
/// todo_write — 全量替换某作用域（goal/task/session）的拆解 TODO 表（设计 2026-09-16 §4，TD-1）。
/// </summary>
[Tool(
    id: "todo_write",
    name: "写入拆解 TODO",
    description: "全量替换某作用域（goal/task/session）的拆解 TODO 表（幂等：按 slug 全量对齐，重跑不产生重复项；返回 diff）。列表按当前 Agent 身份隔离——其他 Agent 即使 scope_id 相同也读不到你这份。【何时用】开始一轮目标/任务前做步骤拆解；受阻或换方向时重新审视全量拆解。【怎么用】scope_kind=goal|task|session + scope_id 定位唯一列表；expected_revision 传 todo_read/todo_write 返回的最新 revision（首写传 0，服务端 CAS 校验，不符返回 todo.version_conflict）；items 为全量目标状态：建议 ≤20 项、建议同时仅 1 项 in_progress、blocked 建议给 blocked_reason（软约束：超限/多开/缺 reason 均接受写入但返回 warnings）；title ≤120 字；slug 列表内唯一（硬拒绝：同 slug 覆盖、缺席即移除、重复 slug 报错）；返回新 revision、diff(added/completed/blocked/removed) 与可选 warnings。【坑】TODO 是 Agent 自述层，不代表目标达成——验收由平台 verifier 裁决；本工具只维护拆解表，不创建看板卡、不改 task_nodes。Full-replace the breakdown TODO list for a scope (idempotent by slug, CAS via expected_revision, returns diff and optional soft-constraint warnings); lists are isolated per calling agent identity.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
    // 2026-08-28 裁定同款：轻量元数据工具（用户原则：仅直接损坏/泄露用户数据需门禁）
public sealed class TodoWriteTool(ITodoStore store)
    : PuddingToolBase<TodoWriteArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TodoWriteArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        try
        {
            var result = await store.WriteAsync(new TodoWriteRequest
            {
                AgentId = context.AgentInstanceId,
                ScopeKind = args.ScopeKind,
                ScopeId = args.ScopeId,
                Title = args.Title,
                ExpectedRevision = args.ExpectedRevision,
                Items = args.Items.Select(i => new TodoItemInput
                {
                    Slug = i.Slug,
                    Title = i.Title,
                    Status = i.Status,
                    Note = i.Note,
                    EvidenceRef = i.EvidenceRef,
                    BlockedReason = i.BlockedReason,
                    OrderIndex = i.OrderIndex,
                }).ToList(),
            }, ct);

            return ToolExecutionResult.Ok(TodoToolJson.Serialize(result));
        }
        catch (TodoStoreException ex)
        {
            return ToolExecutionResult.Fail(TodoToolErrors.BuildErrorJson(ex));
        }
    }
}
