using System.ComponentModel;
using ModelContextProtocol.Server;
using PuddingCodexService.Models;
using PuddingCodexService.Services;

namespace PuddingCodexService.Tools;

[McpServerToolType]
public sealed class CodexTaskTools(
    CodexTaskCoordinator coordinator,
    SupervisorRestartRequestWriter restartWriter)
{
    [McpServerTool(Name = "codex_task_start", UseStructuredContent = true)]
    [Description("Starts a durable engineering task outside Pudding in fixed Yolo mode and immediately returns a taskId. Never use this tool to restart, stop, or launch Pudding; use pudding_self_heal_start for every Pudding repair/rebuild/restart request. Poll with codex_task_get.")]
    public Task<CodexTaskAccepted> StartAsync(
        [Description("Engineering task for Codex.")] string prompt,
        [Description("Working directory inside the configured Pudding repository.")] string? cwd = null,
        [Description("Optional Codex model override.")] string? model = null,
        CancellationToken cancellationToken = default) =>
        coordinator.StartAsync(prompt, cwd, model, cancellationToken);

    [McpServerTool(Name = "pudding_self_heal_start", UseStructuredContent = true)]
    [Description("Starts the required safe Codex workflow for every request to repair, rebuild, self-heal, or restart Pudding. Codex is forbidden from controlling Pudding processes; after Codex succeeds, this service automatically requests an external staged backend-only restart and persists the restart request ID. Poll with codex_task_get.")]
    public Task<CodexTaskAccepted> StartSelfHealAsync(
        [Description("Engineering work to complete before the staged restart. For a restart-only request, ask Codex to validate the repository build without changing code unnecessarily.")] string prompt,
        [Description("Working directory inside the configured Pudding repository.")] string? cwd = null,
        [Description("Optional Codex model override.")] string? model = null,
        CancellationToken cancellationToken = default) =>
        coordinator.StartSelfHealAsync(prompt, cwd, model, cancellationToken);

    [McpServerTool(Name = "codex_task_get", UseStructuredContent = true)]
    [Description("Gets the durable state and final result of a Codex task by taskId.")]
    public async Task<CodexTaskRecord> GetAsync(
        [Description("Task ID returned by codex_task_start or codex_task_reply.")] string taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await coordinator.GetRequiredAsync(taskId, cancellationToken);
        if (string.IsNullOrWhiteSpace(task.RestartRequestId))
            return task;
        return task with
        {
            RestartResultJson = await restartWriter.GetResultAsync(
                task.RestartRequestId,
                cancellationToken),
        };
    }

    [McpServerTool(Name = "codex_task_reply", UseStructuredContent = true)]
    [Description("Continues the Codex thread from a completed task and returns a new durable taskId.")]
    public Task<CodexTaskAccepted> ReplyAsync(
        [Description("Completed task whose Codex thread should continue.")] string taskId,
        [Description("Follow-up instruction for Codex.")] string prompt,
        CancellationToken cancellationToken = default) =>
        coordinator.ReplyAsync(taskId, prompt, cancellationToken);

    [McpServerTool(Name = "codex_task_cancel", UseStructuredContent = true)]
    [Description("Cancels a queued or running Codex task.")]
    public Task<CodexTaskRecord> CancelAsync(
        [Description("Task to cancel.")] string taskId,
        CancellationToken cancellationToken = default) =>
        coordinator.CancelAsync(taskId, cancellationToken);

    [McpServerTool(Name = "pudding_build_restart", UseStructuredContent = true)]
    [Description("Requests an external staged build and backend-only Pudding restart after a completed Codex task. This is a privileged operation.")]
    public Task<PuddingRestartAccepted> RestartAsync(
        [Description("Completed Codex task that produced the patch to build and run.")] string taskId,
        CancellationToken cancellationToken = default) =>
        RequestRestartAsync(taskId, cancellationToken);

    private async Task<PuddingRestartAccepted> RequestRestartAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await coordinator.GetRequiredAsync(taskId, cancellationToken);
        return await restartWriter.RequestAsync(task, cancellationToken);
    }

    [McpServerTool(Name = "pudding_restart_get")]
    [Description("Gets the external supervisor result for a Pudding restart request.")]
    public Task<string> GetRestartAsync(
        [Description("Restart request ID returned by pudding_build_restart.")] string requestId,
        CancellationToken cancellationToken = default) =>
        restartWriter.GetResultAsync(requestId, cancellationToken);
}
