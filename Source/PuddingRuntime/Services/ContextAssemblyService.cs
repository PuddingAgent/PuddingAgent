using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 上下文合成 Facade，包装 ContextPipeline 对外暴露稳定契约。
/// </summary>
public sealed class ContextAssemblyService : IContextAssemblyService
{
    private readonly ContextPipeline _pipeline;
    private readonly ILogger<ContextAssemblyService> _logger;

    public ContextAssemblyService(
        ContextPipeline pipeline,
        ILogger<ContextAssemblyService> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<PuddingCode.Runtime.ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[ContextAssembly] Assemble session={SessionId} agent={AgentTemplateId} maxTokens={MaxTokens} streaming={Streaming} first={First}",
            request.SessionId, request.AgentTemplateId, request.MaxContextTokens, request.ForStreaming, request.IsFirstMessage);

        // 构造最小 AgentTemplateDefinition，确保 ContextPipeline 能读取 Runtime.MaxContextTokens
        var template = new AgentTemplateDefinition
        {
            TemplateId = request.AgentTemplateId ?? "unknown",
            Name = request.AgentTemplateId ?? "Unknown",
            TemplateType = AgentTemplateType.Task,
            Runtime = new RuntimeProfile { MaxContextTokens = request.MaxContextTokens > 0 ? request.MaxContextTokens : 8192 },
        };

        // 适配到现有 ContextPipeline 的输入格式，传递真实会话语义
        var contextRequest = new ContextRequest
        {
            Template = template,
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage = request.UserMessage,
            AgentInstanceId = request.AgentInstanceId,
            ForStreaming = request.ForStreaming,
            IsFirstMessage = request.IsFirstMessage,
            SessionHistory = request.SessionHistory,
            Trace = RuntimeTraceContext.CreateNew(
                sessionId: request.SessionId,
                workspaceId: request.WorkspaceId,
                executionId: request.AgentInstanceId),
            TaskPlanId = request.TaskPlanId,
            TaskNodeId = request.TaskNodeId,
            ParentTaskNodeId = request.ParentTaskNodeId,
            DelegationDepth = request.DelegationDepth,
            MaxDelegationDepth = request.MaxDelegationDepth,
            RoleInPlan = request.RoleInPlan,
            AllowSubDelegation = request.AllowSubDelegation,
                        AllowAgentCreation = request.AllowAgentCreation,
            AssignedObjective = request.AssignedObjective,
            ExpectedOutputContract = request.ExpectedOutputContract,
            ParentContextSnapshot = request.ParentContextSnapshot,
        };

        var pipelineResult = await _pipeline.AssembleAsync(contextRequest, ct);

        // 转换为新契约格式
        var layers = pipelineResult.Layers
            .Select(l => new PuddingCode.Runtime.ContextLayerSummary
            {
                Layer = l.LayerName,
                EstimatedTokens = l.EstimatedTokens,
                ItemCount = 1,
            })
            .ToList();

        // 返回完整消息列表：System prompt + 用户消息
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, pipelineResult.SystemPrompt)
        };
        messages.Add(new ChatMessage(ChatRole.User, request.UserMessage));

        return new PuddingCode.Runtime.ContextAssemblyResult
        {
            Messages = messages,
            EstimatedTokens = pipelineResult.UsedTokens,
            Layers = layers,
        };
    }
}
