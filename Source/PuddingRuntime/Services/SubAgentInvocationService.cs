using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 子代理调用 Facade，隔离父执行循环与子代理生命周期。
/// 子代理可以并发，但并发额度、超时和批量聚合属于运行时基础设施职责；
/// 工具只表达“要派生什么任务”，不能自己实现调度或猜测默认参数。
/// </summary>
public sealed class SubAgentInvocationService : ISubAgentInvocationService
{
    private readonly ISubAgentManager _subAgentManager;
    private readonly IRuntimeExecutionConfigService _config;
    private readonly ILogger<SubAgentInvocationService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _templateGates = new(StringComparer.Ordinal);

    public SubAgentInvocationService(
        ISubAgentManager subAgentManager,
        IRuntimeExecutionConfigService config,
        ILogger<SubAgentInvocationService> logger)
    {
        _subAgentManager = subAgentManager;
        _config = config;
        _logger = logger;
    }

    public async Task<SubAgentInvocationResult> InvokeAsync(SubAgentInvocationRequest request, CancellationToken ct = default)
    {
        request = request with
        {
            InvocationId = string.IsNullOrWhiteSpace(request.InvocationId)
                ? $"subinv-{Guid.NewGuid():N}"
                : request.InvocationId.Trim(),
        };
        _logger.LogDebug(
            "[SubAgentInvocation] Invoke template={TemplateId} task={Task} async={IsAsync} parentSession={ParentSessionId}",
            request.TemplateId, request.Task, request.IsAsync, request.ParentSessionId);

        var options = _config.GetOptions().SubAgents;
        ValidatePermissionMode(request.PermissionMode);
        var timeoutSeconds = ResolveTimeoutSeconds(request.TimeoutSeconds, options);
        var spawnRequest = BuildSpawnRequest(request, timeoutSeconds);

        if (request.IsAsync)
        {
            return await InvokeAsyncSpawnAsync(spawnRequest, ct);
        }
        else
        {
            return await ExecuteWithSchedulerAsync(spawnRequest, options, timeoutSeconds, ct);
        }
    }

    public async Task<SubAgentBatchInvocationResult> InvokeBatchAsync(
        SubAgentBatchInvocationRequest request,
        CancellationToken ct = default)
    {
        var options = _config.GetOptions().SubAgents;
        ValidatePermissionMode(request.PermissionMode);
        ValidateBatchTasks(request.Tasks, options);

        var timeoutSeconds = ResolveTimeoutSeconds(request.TimeoutSeconds, options);
        var batchId = !string.IsNullOrWhiteSpace(request.BatchId)
            ? request.BatchId.Trim()
            : string.IsNullOrWhiteSpace(request.ParentTaskId)
                ? $"subbatch-{Guid.NewGuid():N}"
                : request.ParentTaskId.Trim();

        var childRequests = request.Tasks
            .Select(task => BuildSpawnRequest(request, task, batchId, timeoutSeconds))
            .ToArray();

        if (request.IsAsync)
        {
            var asyncResults = new List<SubAgentInvocationResult>();
            foreach (var child in childRequests)
            {
                asyncResults.Add(await InvokeAsyncSpawnAsync(child, ct));
            }

            return new SubAgentBatchInvocationResult
            {
                BatchId = batchId,
                Status = asyncResults.All(r => r.Status != "failed") ? "running" : "partial_failed",
                Results = asyncResults,
                Summary = BuildBatchSummary(asyncResults),
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var tasks = childRequests
            .Select(child => ExecuteWithSchedulerAsync(child, options, timeoutSeconds, timeoutCts.Token))
            .ToArray();

        SubAgentInvocationResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            results = tasks.Select((task, index) =>
                task.IsCompletedSuccessfully
                    ? task.Result
                    : new SubAgentInvocationResult
                    {
                        SubSessionId = "unknown",
                        TaskId = childRequests[index].TaskNodeId,
                        Status = "timed_out",
                        Error = $"Sub-agent batch timed out after {timeoutSeconds} seconds.",
                    }).ToArray();
        }

        return new SubAgentBatchInvocationResult
        {
            BatchId = batchId,
            Status = ResolveBatchStatus(results),
            Results = results,
            Summary = BuildBatchSummary(results),
        };
    }

    private async Task<SubAgentInvocationResult> InvokeAsyncSpawnAsync(SubAgentSpawnRequest spawnRequest, CancellationToken ct)
    {
        var result = await _subAgentManager.SpawnAsync(spawnRequest, ct);
        return new SubAgentInvocationResult
        {
            SubSessionId = result.SubSessionId,
            RunId = result.RunId,
            TaskId = spawnRequest.TaskNodeId,
            Status = result.Success ? "running" : "failed",
            Error = result.Error,
        };
    }

    private async Task<SubAgentInvocationResult> ExecuteWithSchedulerAsync(
        SubAgentSpawnRequest spawnRequest,
        SubAgentExecutionOptions options,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var workspaceGate = _workspaceGates.GetOrAdd(
            spawnRequest.WorkspaceId,
            _ => new SemaphoreSlim(options.MaxConcurrentPerWorkspace));
        var templateGate = _templateGates.GetOrAdd(
            $"{spawnRequest.WorkspaceId}\u001f{spawnRequest.TemplateId}",
            _ => new SemaphoreSlim(options.MaxConcurrentPerTemplate));

        await workspaceGate.WaitAsync(ct);
        await templateGate.WaitAsync(ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var result = await _subAgentManager.ExecuteSyncAsync(spawnRequest, timeoutCts.Token);
                return new SubAgentInvocationResult
                {
                    SubSessionId = result.SubSessionId,
                    RunId = result.RunId,
                    TaskId = spawnRequest.TaskNodeId,
                    Status = result.Status ?? (result.Success ? "completed" : "failed"),
                    Reply = result.Reply,
                    Error = result.Error,
                };
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new SubAgentInvocationResult
                {
                    SubSessionId = "unknown",
                    TaskId = spawnRequest.TaskNodeId,
                    Status = "timed_out",
                    Error = $"Sub-agent timed out after {timeoutSeconds} seconds.",
                };
            }
        }
        finally
        {
            templateGate.Release();
            workspaceGate.Release();
        }
    }

    private static SubAgentSpawnRequest BuildSpawnRequest(SubAgentInvocationRequest request, int timeoutSeconds) => new()
    {
        ParentSessionId = request.ParentSessionId,
        ParentAgentId = request.ParentAgentId,
        ConfigurationAgentInstanceId = request.ParentAgentInstanceId,
        WorkspaceId = request.WorkspaceId,
        WorkingDirectory = request.WorkingDirectory,
        TaskDescription = request.Task,
        TemplateId = request.TemplateId,
        LlmConfig = request.LlmConfig,
                LlmProfile = request.LlmProfile,
        ParentContextSnapshot = request.ParentContextSnapshot,
        MaxRounds = request.MaxRounds ?? 10,
        CapabilityPolicy = request.CapabilityPolicy,
        TaskPlanId = request.TaskPlanId,
        TaskNodeId = request.TaskNodeId,
        ParentTaskNodeId = request.ParentTaskNodeId,
        DelegationDepth = request.DelegationDepth,
        MaxDelegationDepth = request.MaxDelegationDepth,
        RoleInPlan = request.RoleInPlan,
        AllowSubDelegation = request.AllowSubDelegation,
        AllowAgentCreation = request.AllowAgentCreation,
        AssignedObjective = request.AssignedObjective,
        ExpectedOutputContract = request.OutputContract ?? request.ExpectedOutputContract,
        TimeoutSeconds = timeoutSeconds,
        InvocationId = request.InvocationId,
        OriginToolId = request.OriginToolId,
        ParentExecutionIdentity = request.ParentExecutionIdentity,
    };

    private static SubAgentSpawnRequest BuildSpawnRequest(
        SubAgentBatchInvocationRequest request,
        SubAgentBatchTask task,
        string batchId,
        int timeoutSeconds) => new()
    {
        ParentSessionId = request.ParentSessionId,
        ParentAgentId = request.ParentAgentId,
        ConfigurationAgentInstanceId = request.ParentAgentInstanceId,
        WorkspaceId = request.WorkspaceId,
        WorkingDirectory = request.WorkingDirectory,
        TaskDescription = task.Task,
        TemplateId = request.TemplateId,
        LlmConfig = request.LlmConfig,
                LlmProfile = request.LlmProfile,
        ParentContextSnapshot = request.ParentContextSnapshot,
        MaxRounds = request.MaxRounds ?? 10,
        CapabilityPolicy = request.CapabilityPolicy,
        TaskPlanId = request.TaskPlanId,
        TaskNodeId = task.TaskId,
        ParentTaskNodeId = request.ParentTaskNodeId,
        DelegationDepth = request.DelegationDepth,
        MaxDelegationDepth = request.MaxDelegationDepth,
        RoleInPlan = request.RoleInPlan,
        AllowSubDelegation = request.AllowSubDelegation,
        AllowAgentCreation = request.AllowAgentCreation,
        AssignedObjective = task.Task,
        ExpectedOutputContract = task.OutputContract ?? task.ExpectedOutput,
        TimeoutSeconds = timeoutSeconds,
        InvocationId = $"subinv-{Guid.NewGuid():N}",
        BatchId = batchId,
        OriginToolId = request.OriginToolId,
        ParentExecutionIdentity = request.ParentExecutionIdentity,
    };

    private static int ResolveTimeoutSeconds(int? requested, SubAgentExecutionOptions options)
    {
        var value = requested ?? options.DefaultTimeoutSeconds;
        if (value <= 0)
            throw new InvalidOperationException("Sub-agent timeout_seconds must be greater than 0.");
        if (value > options.MaxTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"Sub-agent timeout_seconds={value} exceeds configured maxTimeoutSeconds={options.MaxTimeoutSeconds}.");
        }

        return value;
    }

    private static void ValidatePermissionMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return;

        if (string.Equals(mode, SubAgentPermissionModes.Inherit, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, SubAgentPermissionModes.Low, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Invalid sub-agent permission_mode '{mode}'. Valid values: inherit, low.");
    }

    private static void ValidateBatchTasks(IReadOnlyList<SubAgentBatchTask> tasks, SubAgentExecutionOptions options)
    {
        if (tasks.Count == 0)
            throw new InvalidOperationException("Batch sub-agent invocation requires at least one task.");
        if (tasks.Count > options.MaxConcurrentPerTemplate)
        {
            throw new InvalidOperationException(
                $"Batch sub-agent invocation contains {tasks.Count} tasks, exceeding maxConcurrentPerTemplate={options.MaxConcurrentPerTemplate}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId))
                throw new InvalidOperationException("Every batch task requires task_id.");
            if (!seen.Add(task.TaskId))
                throw new InvalidOperationException($"Duplicate batch task_id '{task.TaskId}'.");
            if (string.IsNullOrWhiteSpace(task.Task))
                throw new InvalidOperationException($"Batch task '{task.TaskId}' has empty task text.");
        }
    }

    private static string ResolveBatchStatus(IReadOnlyList<SubAgentInvocationResult> results)
    {
        if (results.Any(r => r.Status == "timed_out"))
            return "timed_out";
        if (results.All(r => r.Status == "completed"))
            return "completed";
        if (results.Any(r => r.Status == "failed"))
            return "partial_failed";
        return "running";
    }

    private static string BuildBatchSummary(IReadOnlyList<SubAgentInvocationResult> results)
    {
        var completed = results.Count(r => r.Status == "completed");
        var running = results.Count(r => r.Status == "running");
        var failed = results.Count(r => r.Status == "failed");
        var timedOut = results.Count(r => r.Status == "timed_out");
        return $"{results.Count} sub-agents: {completed} completed, {running} running, {failed} failed, {timedOut} timed out.";
    }
}
