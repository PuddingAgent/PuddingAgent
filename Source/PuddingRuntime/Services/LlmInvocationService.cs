using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntime.Services;

/// <summary>
/// LLM 调用 Facade，包装 IRuntimeLlmClient 对外暴露稳定契约。
/// </summary>
public sealed class LlmInvocationService : ILlmInvocationService
{
    private readonly IRuntimeLlmClient _llmClient;
    private readonly ILlmProfileResolver? _profileResolver;
    private readonly ILogger<LlmInvocationService> _logger;

    public LlmInvocationService(
        IRuntimeLlmClient llmClient,
        ILogger<LlmInvocationService> logger,
        ILlmProfileResolver? profileResolver = null)
    {
        _llmClient = llmClient;
        _logger = logger;
        _profileResolver = profileResolver;
    }

    public async Task<LlmInvocationResult> InvokeAsync(LlmInvocationRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[LlmInvocation] Invoke session={SessionId} provider={ProviderId} profile={ProfileId} model={ModelId} msgCount={MsgCount} toolCount={ToolCount}",
            request.SessionId, request.Profile.ProviderId, request.Profile.ProfileId, request.Profile.ModelId,
            request.Messages.Count, request.Tools.Count);

        try
        {
            LlmConfig llmConfig;
            if (_profileResolver is not null)
            {
                llmConfig = await _profileResolver.ResolveAsync(
                    request.WorkspaceId, request.AgentInstanceId, request.Profile, ct);
            }
            else
            {
                llmConfig = new LlmConfig { ModelId = request.Profile.ModelId };
            }

            var response = await _llmClient.ChatAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.Messages,
                request.Tools.Count > 0 ? request.Tools : null,
                llmConfig,
                ct);

            return new LlmInvocationResult
            {
                Success = true,
                ReplyText = response.Content,
                ToolCalls = response.ToolCalls ?? Array.Empty<ToolCall>(),
                Usage = response.Usage,
                ProviderId = request.Profile.ProviderId,
                ModelId = request.Profile.ModelId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LlmInvocation] LLM API error session={SessionId}", request.SessionId);
            return new LlmInvocationResult
            {
                Success = false,
                Error = ex.Message,
            };
        }
    }

    public async IAsyncEnumerable<StreamDelta> InvokeStreamAsync(
        LlmInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LlmConfig llmConfig;
        if (_profileResolver is not null)
        {
            llmConfig = await _profileResolver.ResolveAsync(
                request.WorkspaceId, request.AgentInstanceId, request.Profile, ct);
        }
        else
        {
            llmConfig = new LlmConfig { ModelId = request.Profile.ModelId };
        }

        await foreach (var delta in _llmClient.ChatStreamAsync(
            request.WorkspaceId,
            request.SessionId,
            request.AgentTemplateId,
            request.Messages,
            request.Tools.Count > 0 ? request.Tools : null,
            llmConfig,
            ct))
        {
            yield return delta;
        }
    }
}
