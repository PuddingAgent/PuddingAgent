using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Runtime;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingPlatform.Services.Tasks;
using PuddingRuntime.Services.TaskTools;

namespace PuddingRuntimeTests.Services.TaskE2E;

/// <summary>
/// TB-08-C 测试替身 <see cref="IToolInvocationService"/>：捕获每次请求，并把 task_* 工具
/// 分发到真实工具实现（真实 <see cref="TaskAgentCommandService"/> + SQLite），
/// 回写链构造与生产 <c>PuddingRuntime.Services.ToolInvocationService</c>（:78-100）同构。
/// </summary>
public sealed class TaskToolInvocationService : IToolInvocationService
{
    private readonly TaskAgentCommandService _commandService;
    private readonly IOptions<WorkspaceTaskFeatureOptions> _options;

    public TaskToolInvocationService(TaskAgentCommandService commandService)
    {
        _commandService = commandService;
        _options = Options.Create(new WorkspaceTaskFeatureOptions { Enabled = true });
    }

    /// <summary>按调用顺序捕获的 <see cref="ToolInvocationRequest"/>（等价 B2 RecordingToolInvocationService）。</summary>
    public List<ToolInvocationRequest> Captured { get; } = [];

    /// <summary>与 <see cref="Captured"/> 一一对应的 <see cref="ToolInvocationResult"/>（供断言 error code）。</summary>
    public List<ToolInvocationResult> Results { get; } = [];

    public async Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken ct = default)
    {
        Captured.Add(request);

        IPuddingTool tool = request.ToolName switch
        {
            "task_list" => new TaskListTool(_commandService, _options, NullLogger<TaskListTool>.Instance),
            "task_get" => new TaskGetTool(_commandService, _options, NullLogger<TaskGetTool>.Instance),
            "task_claim" => new TaskClaimTool(_commandService, _options, NullLogger<TaskClaimTool>.Instance),
            "task_update" => new TaskUpdateTool(_commandService, _options, NullLogger<TaskUpdateTool>.Instance),
            _ => throw new NotSupportedException(
                $"E2E harness only routes task_* tools, got '{request.ToolName}'."),
        };

        // 回写链：与 ToolInvocationService.cs:78-100 同构（ActiveTask 透传，ExecutionIdentity 冻结 ToolCallId）。
        var context = new ToolExecutionContext
        {
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentInstanceId = request.AgentInstanceId,
            ConfigurationAgentInstanceId = request.ConfigurationAgentInstanceId,
            WorkingDirectory = request.WorkingDirectory,
            AgentTemplateId = request.AgentTemplateId,
            Trace = request.Trace,
            ToolCallId = request.ToolCallId,
            ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
            DelegationDepth = request.DelegationDepth,
            MaxDelegationDepth = request.MaxDelegationDepth,
            AllowSubDelegation = request.AllowSubDelegation,
            RoleInPlan = request.RoleInPlan,
            CapabilityPolicy = request.CapabilityPolicy,
            ExecutionIdentity = request.ExecutionIdentity is null
                ? null
                : request.ExecutionIdentity with { ToolCallId = request.ToolCallId },
            ActiveTask = request.ActiveTask,
        };

        var executionResult = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = request.ToolCallId,
            ArgumentsJson = request.ArgumentsJson,
            Context = context,
        }, ct);

        var invocationResult = new ToolInvocationResult
        {
            Success = executionResult.Success,
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Output = executionResult.Output,
            Error = executionResult.Error,
            DurationMs = 0,
            OutputLength = executionResult.Output?.Length ?? 0,
        };

        Results.Add(invocationResult);
        return invocationResult;
    }
}
