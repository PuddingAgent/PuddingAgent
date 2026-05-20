using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntime.Services;

/// <summary>
/// 上下文合成 Facade，包装 ContextPipeline 对外暴露稳定契约。
/// 第一阶段：仅适配现有 ContextPipeline.AssembleAsync，不改变内部实现。
/// </summary>
public sealed class ContextAssemblyService : IContextAssemblyService
{
    private readonly ContextPipeline _pipeline;
    private readonly ILogger<ContextAssemblyService> _logger;

    public ContextAssemblyService(ContextPipeline pipeline, ILogger<ContextAssemblyService> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<PuddingCode.Runtime.ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[ContextAssembly] Assemble session={SessionId} agent={AgentTemplateId} maxTokens={MaxTokens}",
            request.SessionId, request.AgentTemplateId, request.MaxContextTokens);

        // 适配到现有 ContextPipeline 的输入格式
        var contextRequest = new ContextRequest
        {
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage = request.UserMessage,
            AgentInstanceId = request.AgentInstanceId,
            ForStreaming = false,
            IsFirstMessage = true,
            SessionHistory = Array.Empty<ChatMessage>(),
        };

        var pipelineResult = await _pipeline.AssembleAsync(contextRequest, ct);

        // 转换为新契约格式
        var layers = pipelineResult.Layers
            .Select(l => new ContextLayerSummary
            {
                Layer = l.LayerName,
                EstimatedTokens = l.EstimatedTokens,
                ItemCount = 1,
            })
            .ToList();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, pipelineResult.SystemPrompt)
        };

        return new PuddingCode.Runtime.ContextAssemblyResult
        {
            Messages = messages,
            EstimatedTokens = pipelineResult.UsedTokens,
            Layers = layers,
        };
    }
}
