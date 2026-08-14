using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
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
    /// <summary>P0-1: 单层正文入日志的尺寸上限（64KB）。</summary>
    internal const int MaxLayerContentBytes = 65536;

    private readonly ContextPipeline _pipeline;
    private readonly ILogger<ContextAssemblyService> _logger;
    private readonly IContextAssemblyEventEmitter? _contextAssemblyEventEmitter;
    private readonly IKeyVaultService? _keyVaultService;

    public ContextAssemblyService(
        ContextPipeline pipeline,
        ILogger<ContextAssemblyService> logger,
        IContextAssemblyEventEmitter? contextAssemblyEventEmitter = null,
        IKeyVaultService? keyVaultService = null)
    {
        _pipeline = pipeline;
        _logger = logger;
        _contextAssemblyEventEmitter = contextAssemblyEventEmitter;
        _keyVaultService = keyVaultService;
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

        // ── P0-1: 发射 context.assembled 事件（各层正文脱敏后入日志）──
        await EmitContextAssembledEventAsync(request, pipelineResult.LayerInfos, ct);

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

    /// <summary>
    /// P0-1: 将 pipeline 层正文逐层脱敏/哈希/截断，并 fire-and-forget 发射 context.assembled 事件。
    /// 任何失败只记录日志，绝不向上抛出，不影响 AssembleAsync 返回结果。
    /// </summary>
    private async Task EmitContextAssembledEventAsync(
        ContextAssemblyRequest request,
        IReadOnlyList<ContextLayerInfo>? layerInfos,
        CancellationToken ct)
    {
        if (_contextAssemblyEventEmitter is null || layerInfos is null || layerInfos.Count == 0)
            return;

        try
        {
            var layers = new List<ContextAssemblyLayerEmission>(layerInfos.Count);
            foreach (var layer in layerInfos)
            {
                layers.Add(await BuildLayerEmissionAsync(layer, _keyVaultService, ct));
            }

            var assembledAtIso = DateTimeOffset.UtcNow.ToString("O");
            // fire-and-forget：不 await 阻塞返回；发射器内部已吞异常。
            _ = _contextAssemblyEventEmitter.EmitAsync(
                request.SessionId,
                request.WorkspaceId,
                request.AgentInstanceId,
                turnId: null,
                traceId: request.TraceId,
                layers,
                assembledAtIso,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextAssembly] Failed to emit context.assembled session={SessionId} agent={AgentId}",
                request.SessionId, request.AgentInstanceId);
        }
    }

    /// <summary>P0-1: 对单层正文做脱敏、SHA-256、64KB 截断，生成发射载荷。</summary>
    internal static async Task<ContextAssemblyLayerEmission> BuildLayerEmissionAsync(
        ContextLayerInfo layer,
        IKeyVaultService? keyVaultService,
        CancellationToken ct)
    {
        var original = layer.FullContent ?? string.Empty;

        // 脱敏（无 KeyVault 时保留原文）
        var content = original;
        if (keyVaultService is not null && !string.IsNullOrEmpty(original))
        {
            content = await keyVaultService.StripAsync(original, ct) ?? string.Empty;
        }

        // SHA-256 hex（小写），对脱敏后全文计算
        var contentHash = ComputeSha256Hex(content);

        // 64KB 截断
        var truncated = false;
        if (Encoding.UTF8.GetByteCount(content) > MaxLayerContentBytes)
        {
            content = TruncateToUtf8ByteLimit(content, MaxLayerContentBytes);
            truncated = true;
        }

        return new ContextAssemblyLayerEmission(layer.LayerName, contentHash, content, truncated);
    }

    /// <summary>SHA-256 hex（小写）。</summary>
    internal static string ComputeSha256Hex(string text)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>按 UTF-8 字节上限截断，绝不拆分码点。</summary>
    internal static string TruncateToUtf8ByteLimit(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var len = rune.Utf8SequenceLength;
            if (bytes + len > maxBytes)
                break;
            sb.Append(rune.ToString());
            bytes += len;
        }
        return sb.ToString();
    }
}
