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
    [McpServerTool(Name = "codex_task_start")]
    [Description("Starts a durable Codex task outside the Pudding process and immediately returns a taskId. Poll with codex_task_get.")]
    public Task<CodexTaskAccepted> StartAsync(
        [Description("Engineering task for Codex.")] string prompt,
        [Description("Working directory inside the configured Pudding repository.")] string? cwd = null,
        [Description("Optional Codex model override.")] string? model = null,
        [Description("Codex sandbox: read-only, workspace-write, or danger-full-access.")] string sandbox = "workspace-write",
        [Description("Codex approval policy: untrusted, on-failure, on-request, or never.")] string approvalPolicy = "never",
        CancellationToken cancellationToken = default) =>
        coordinator.StartAsync(prompt, cwd, model, sandbox, approvalPolicy, cancellationToken);

    [McpServerTool(Name = "codex_task_get")]
    [Description("Gets the durable state and final result of a Codex task by taskId.")]
    public async Task<CodexTaskRecord> GetAsync(
        [Description("Task ID returned by codex_task_start or codex_task_reply.")] string taskId,
        CancellationToken cancellationToken = default) =>
        await coordinator.GetRequiredAsync(taskId, cancellationToken);

    [McpServerTool(Name = "codex_task_reply")]
    [Description("Continues the Codex thread from a completed task and returns a new durable taskId.")]
    public Task<CodexTaskAccepted> ReplyAsync(
        [Description("Completed task whose Codex thread should continue.")] string taskId,
        [Description("Follow-up instruction for Codex.")] string prompt,
        CancellationToken cancellationToken = default) =>
        coordinator.ReplyAsync(taskId, prompt, cancellationToken);

    [McpServerTool(Name = "codex_task_cancel")]
    [Description("Cancels a queued or running Codex task.")]
    public Task<CodexTaskRecord> CancelAsync(
        [Description("Task to cancel.")] string taskId,
        CancellationToken cancellationToken = default) =>
        coordinator.CancelAsync(taskId, cancellationToken);

    [McpServerTool(Name = "pudding_build_restart")]
    [Description("Requests an external staged build and backend-only Pudding restart after a completed Codex task. This is a privileged operation.")]
    public Task<PuddingRestartAccepted> RestartAsync(
        [Description("Completed Codex task that produced the patch to build and run.")] string taskId,
        CancellationToken cancellationToken = default) =>
        restartWriter.RequestAsync(taskId, cancellationToken);

    [McpServerTool(Name = "pudding_restart_get")]
    [Description("Gets the external supervisor result for a Pudding restart request.")]
    public Task<string> GetRestartAsync(
        [Description("Restart request ID returned by pudding_build_restart.")] string requestId,
        CancellationToken cancellationToken = default) =>
        restartWriter.GetResultAsync(requestId, cancellationToken);
}
