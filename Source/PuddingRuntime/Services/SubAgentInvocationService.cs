using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 子代理调用 Facade，隔离父执行循环与子代理生命周期。
/// 第一阶段：包装 ISubAgentManager，不改变行为。
/// </summary>
public sealed class SubAgentInvocationService : ISubAgentInvocationService
{
    private readonly ISubAgentManager _subAgentManager;
    private readonly ILogger<SubAgentInvocationService> _logger;

    public SubAgentInvocationService(ISubAgentManager subAgentManager, ILogger<SubAgentInvocationService> logger)
    {
        _subAgentManager = subAgentManager;
        _logger = logger;
    }

    public async Task<SubAgentInvocationResult> InvokeAsync(SubAgentInvocationRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[SubAgentInvocation] Invoke template={TemplateId} task={Task} async={IsAsync} parentSession={ParentSessionId}",
            request.TemplateId, request.Task, request.IsAsync, request.ParentSessionId);

        var spawnRequest = new SubAgentSpawnRequest
        {
            ParentSessionId = request.ParentSessionId,
            ParentAgentId = request.ParentAgentId,
            WorkspaceId = request.WorkspaceId,
            TaskDescription = request.Task,
            TemplateId = request.TemplateId,
            ModelId = request.ModelId,
            LlmConfig = request.LlmConfig,
            MaxRounds = request.MaxRounds ?? 10,
            CapabilityPolicy = request.CapabilityPolicy,
        };

        if (request.IsAsync)
        {
            var result = await _subAgentManager.SpawnAsync(spawnRequest, ct);
            return new SubAgentInvocationResult
            {
                SubSessionId = result.SubSessionId,
                Status = result.Success ? "running" : "failed",
                Error = result.Error,
            };
        }
        else
        {
            var result = await _subAgentManager.ExecuteSyncAsync(spawnRequest, ct);
            return new SubAgentInvocationResult
            {
                SubSessionId = result.SubSessionId,
                Status = result.Success ? "completed" : "failed",
                Reply = result.Reply,
                Error = result.Error,
            };
        }
    }
}
