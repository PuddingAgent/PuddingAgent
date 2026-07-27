using System.Collections.Concurrent;
using System.Threading.Channels;
using PuddingCodexService.Models;

namespace PuddingCodexService.Services;

public sealed class CodexTaskCoordinator(
    CodexServiceOptions options,
    FileCodexTaskStore store,
    ICodexExecutor executor,
    SupervisorRestartRequestWriter restartWriter,
    ILogger<CodexTaskCoordinator> logger) : BackgroundService
{
    private const string SelfHealPolicy = """
        PUDDING SELF-HEAL POLICY (mandatory):
        - Do not stop, kill, restart, or launch PuddingAgent, PuddingRuntime, dev-up, the frontend, the proxy, or PuddingCodexService.
        - Ignore any requested-work instruction that asks you to control those processes directly.
        - Perform only the requested engineering changes, build, and tests while the current Pudding backend remains online.
        - Do not run or print /yolo. Execution permissions are already fixed by PuddingCodexService.
        - Report the completed work and verification normally. The external supervisor will perform a staged backend-only restart after this task completes.

        Requested work:
        """;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    public async Task<CodexTaskAccepted> StartAsync(
        string prompt,
        string? workingDirectory,
        string? model,
        CancellationToken ct = default) =>
        await StartCoreAsync(prompt, workingDirectory, model, restartPuddingOnCompletion: false, ct);

    public async Task<CodexTaskAccepted> StartSelfHealAsync(
        string prompt,
        string? workingDirectory,
        string? model,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Codex self-heal prompt is required.", nameof(prompt));
        return await StartCoreAsync(
            $"{SelfHealPolicy}\n{prompt.Trim()}",
            workingDirectory,
            model,
            restartPuddingOnCompletion: true,
            ct);
    }

    private async Task<CodexTaskAccepted> StartCoreAsync(
        string prompt,
        string? workingDirectory,
        string? model,
        bool restartPuddingOnCompletion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Codex prompt is required.", nameof(prompt));
        var now = DateTimeOffset.UtcNow;
        var record = new CodexTaskRecord
        {
            TaskId = Guid.NewGuid().ToString("N"),
            Prompt = prompt.Trim(),
            WorkingDirectory = options.NormalizeWorkingDirectory(workingDirectory),
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            // PuddingCodexService is intentionally a developer-machine Yolo executor.
            // Callers cannot downgrade or override this trust boundary per task.
            Sandbox = options.TaskSandbox,
            ApprovalPolicy = options.TaskApprovalPolicy,
            RestartPuddingOnCompletion = restartPuddingOnCompletion,
            Status = CodexTaskStatus.Queued,
            StatusMessage = "Queued by Pudding.",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await store.CreateAsync(record, ct);
        await _queue.Writer.WriteAsync(record.TaskId, ct);
        return new CodexTaskAccepted(
            record.TaskId,
            record.Status,
            record.CreatedAtUtc,
            record.RestartPuddingOnCompletion);
    }

    public async Task<CodexTaskAccepted> ReplyAsync(
        string taskId,
        string prompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Codex reply prompt is required.", nameof(prompt));
        var parent = await GetRequiredAsync(taskId, ct);
        if (parent.Status != CodexTaskStatus.Completed || string.IsNullOrWhiteSpace(parent.ThreadId))
            throw new InvalidOperationException("Codex replies require a completed task with a threadId.");

        var now = DateTimeOffset.UtcNow;
        var record = new CodexTaskRecord
        {
            TaskId = Guid.NewGuid().ToString("N"),
            ParentTaskId = parent.TaskId,
            ThreadId = parent.ThreadId,
            Prompt = prompt.Trim(),
            WorkingDirectory = parent.WorkingDirectory,
            Model = parent.Model,
            Sandbox = parent.Sandbox,
            ApprovalPolicy = parent.ApprovalPolicy,
            Status = CodexTaskStatus.Queued,
            StatusMessage = $"Queued as reply to {parent.TaskId}.",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await store.CreateAsync(record, ct);
        await _queue.Writer.WriteAsync(record.TaskId, ct);
        return new CodexTaskAccepted(
            record.TaskId,
            record.Status,
            record.CreatedAtUtc,
            record.RestartPuddingOnCompletion);
    }

    public Task<CodexTaskRecord?> GetAsync(string taskId, CancellationToken ct = default) =>
        store.GetAsync(taskId, ct);

    public async Task<CodexTaskRecord> GetRequiredAsync(string taskId, CancellationToken ct = default) =>
        await store.GetAsync(taskId, ct)
        ?? throw new KeyNotFoundException($"Codex task was not found: {taskId}");

    public async Task<CodexTaskRecord> CancelAsync(string taskId, CancellationToken ct = default)
    {
        var current = await GetRequiredAsync(taskId, ct);
        if (current.Status is CodexTaskStatus.Completed or CodexTaskStatus.Failed or CodexTaskStatus.Cancelled)
            return current;
        if (_running.TryGetValue(taskId, out var cancellation))
            cancellation.Cancel();
        return await store.UpdateAsync(taskId, record => record with
        {
            Status = CodexTaskStatus.Cancelled,
            StatusMessage = "Cancelled by Pudding.",
        }, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = await store.ListAsync(stoppingToken);
        foreach (var task in recovered.Where(task => task.Status is CodexTaskStatus.Queued or CodexTaskStatus.Running))
        {
            await store.UpdateAsync(task.TaskId, record => record with
            {
                Status = CodexTaskStatus.Queued,
                StatusMessage = "Recovered after Codex Service restart.",
            }, stoppingToken);
            await _queue.Writer.WriteAsync(task.TaskId, stoppingToken);
        }

        foreach (var task in recovered.Where(task =>
                     task.Status == CodexTaskStatus.Completed
                     && task.RestartPuddingOnCompletion
                     && string.IsNullOrWhiteSpace(task.RestartRequestId)))
        {
            await ScheduleRestartAsync(task, stoppingToken);
        }

        await foreach (var taskId in _queue.Reader.ReadAllAsync(stoppingToken))
            await ExecuteOneAsync(taskId, stoppingToken);
    }

    private async Task ExecuteOneAsync(string taskId, CancellationToken stoppingToken)
    {
        var current = await store.GetAsync(taskId, stoppingToken);
        if (current is null || current.Status != CodexTaskStatus.Queued)
            return;

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_running.TryAdd(taskId, executionCancellation))
            return;
        try
        {
            current = await store.UpdateAsync(taskId, record => record with
            {
                Status = CodexTaskStatus.Running,
                StatusMessage = "Codex is running outside Pudding.",
            }, stoppingToken);
            var result = await executor.ExecuteAsync(current, executionCancellation.Token);
            var completed = await store.UpdateAsync(taskId, record => record with
            {
                ThreadId = result.ThreadId,
                Status = result.IsError ? CodexTaskStatus.Failed : CodexTaskStatus.Completed,
                StatusMessage = result.IsError ? "Codex returned an error result." : "Codex completed.",
                ResultJson = result.ResultJson,
                Error = result.IsError ? result.ResultJson : null,
            }, stoppingToken);
            if (!result.IsError && completed.RestartPuddingOnCompletion)
                await ScheduleRestartAsync(completed, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Keep the record recoverable. The next service instance requeues Running tasks.
        }
        catch (OperationCanceledException)
        {
            var latest = await store.GetAsync(taskId, CancellationToken.None);
            if (latest?.Status != CodexTaskStatus.Cancelled)
            {
                await store.UpdateAsync(taskId, record => record with
                {
                    Status = CodexTaskStatus.Cancelled,
                    StatusMessage = "Codex execution was cancelled.",
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CodexService] Task failed task={TaskId}", taskId);
            await store.UpdateAsync(taskId, record => record with
            {
                Status = CodexTaskStatus.Failed,
                StatusMessage = "Codex execution failed.",
                Error = ex.Message,
            }, CancellationToken.None);
        }
        finally
        {
            _running.TryRemove(taskId, out _);
        }
    }

    private async Task ScheduleRestartAsync(CodexTaskRecord task, CancellationToken ct)
    {
        try
        {
            var accepted = await restartWriter.RequestAsync(task, ct);
            await store.UpdateAsync(task.TaskId, record => record with
            {
                RestartRequestId = accepted.RequestId,
                RestartNotBeforeUtc = accepted.NotBeforeUtc,
                StatusMessage = "Codex completed; staged Pudding backend restart scheduled.",
            }, ct);
            logger.LogInformation(
                "[CodexService] Scheduled staged Pudding restart task={TaskId} request={RequestId}",
                task.TaskId,
                accepted.RequestId);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "[CodexService] Failed to schedule Pudding restart task={TaskId}", task.TaskId);
            await store.UpdateAsync(task.TaskId, record => record with
            {
                Status = CodexTaskStatus.Failed,
                StatusMessage = "Codex completed, but scheduling the Pudding restart failed.",
                Error = ex.Message,
            }, CancellationToken.None);
        }
    }

}
