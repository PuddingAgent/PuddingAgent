using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Tasks;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// task_list — 列出当前 Agent 的任务（mine 范围，keyset 分页）。
/// </summary>
[Tool(
    id: "task_list",
    name: "列出工作区任务",
    description: "列出分配给当前 Agent 的工作区任务（mine 范围）。【何时用】需要查看自己名下任务时使用。【怎么用】可选 status（Backlog/Ready/Deferred/Reserved/Assigned/NeedsReview/InProgress/Blocked/Completed/Failed/Cancelled/Archived）、board_column（Backlog/Todo/InProgress/Done/Failed，与 status 互斥）、priority（p0/p1/p2/p3）、limit（1..100，默认 50）、cursor（分页游标，取上次返回的 next_cursor）。【坑】只返回 active assignment 属于自己的任务；默认不含 Archived/Cancelled；workspace_id 由运行时注入，不接受 Agent 指定。List the current agent's assigned workspace tasks (mine scope) with keyset pagination; filter by status/board_column/priority, page via limit+cursor.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Medium)]
public sealed class TaskListTool : PuddingToolBase<TaskListArgs>
{
    private readonly ITaskAgentCommandService _service;
    private readonly IOptions<WorkspaceTaskFeatureOptions> _options;

    public TaskListTool(
        ITaskAgentCommandService service,
        IOptions<WorkspaceTaskFeatureOptions> options,
        ILogger<TaskListTool> logger)
    {
        _service = service;
        _options = options;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TaskListArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!_options.Value.Enabled)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(
                TaskErrorCode.CapabilityMissing,
                "Workspace task tools are disabled (WorkspaceTasks.Enabled=false)."));
        }

        try
        {
            var result = await _service.ListMineAsync(new TaskAgentListQuery
            {
                WorkspaceId = context.WorkspaceId,
                AgentId = context.AgentInstanceId,
                Status = args.Status,
                BoardColumn = args.BoardColumn,
                Priority = args.Priority,
                Limit = args.Limit ?? 50,
                Cursor = args.Cursor,
            }, ct);

            return ToolExecutionResult.Ok(TaskToolJson.Serialize(result));
        }
        catch (TaskStoreException ex)
        {
            return ToolExecutionResult.Fail(TaskToolErrors.BuildErrorJson(ex));
        }
    }
}
