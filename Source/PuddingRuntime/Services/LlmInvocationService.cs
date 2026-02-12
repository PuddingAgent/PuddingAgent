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
            "[LlmInvocation] Invoke session={SessionId} provider={ProviderId} profile={ProfileId} model={ModelId} msgCount={MsgCount} toolCount={ToolCount} prefix={PrefixHash}",
            request.SessionId, request.Profile.ProviderId, request.Profile.ProfileId, request.Profile.ModelId,
            request.Messages.Count, request.Tools.Count, request.PrefixSnapshot?.PrefixHash ?? "(none)");

        try
        {
            ResolvedLlmInvocationProfile resolved;
            if (_profileResolver is not null)
            {
                resolved = await _profileResolver.ResolveAsync(
                    request.WorkspaceId, request.AgentInstanceId, request.Profile, ct);
                resolved = ApplyConfigOverride(resolved, request.ConfigOverride);
            }
            else
            {
                resolved = new ResolvedLlmInvocationProfile
                {
                    ProviderId = request.Profile.ProviderId,
                    ProfileId = request.Profile.ProfileId,
                    ModelId = request.Profile.ModelId,
                    Role = request.Profile.Role,
                    Config = new LlmConfig { ModelId = request.Profile.ModelId },
                };
                resolved = ApplyConfigOverride(resolved, request.ConfigOverride);
            }

            var response = await _llmClient.ChatAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.Messages,
                request.Tools.Count > 0 ? request.Tools : null,
                resolved.Config,
                ct);

            return new LlmInvocationResult
            {
                Success = true,
                ReplyText = response.Content,
                ToolCalls = response.ToolCalls ?? Array.Empty<ToolCall>(),
                Usage = response.Usage,
                ProviderId = resolved.ProviderId,
                ModelId = resolved.ModelId,
                PrefixSnapshot = request.PrefixSnapshot,
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
        ResolvedLlmInvocationProfile resolved;
        _logger.LogDebug(
            "[LlmInvocation] Stream session={SessionId} provider={ProviderId} profile={ProfileId} model={ModelId} msgCount={MsgCount} toolCount={ToolCount} prefix={PrefixHash}",
            request.SessionId, request.Profile.ProviderId, request.Profile.ProfileId, request.Profile.ModelId,
            request.Messages.Count, request.Tools.Count, request.PrefixSnapshot?.PrefixHash ?? "(none)");

        if (_profileResolver is not null)
        {
            resolved = await _profileResolver.ResolveAsync(
                request.WorkspaceId, request.AgentInstanceId, request.Profile, ct);
            resolved = ApplyConfigOverride(resolved, request.ConfigOverride);
        }
        else
        {
            resolved = new ResolvedLlmInvocationProfile
            {
                ProviderId = request.Profile.ProviderId,
                ProfileId = request.Profile.ProfileId,
                ModelId = request.Profile.ModelId,
                Role = request.Profile.Role,
                Config = new LlmConfig { ModelId = request.Profile.ModelId },
            };
            resolved = ApplyConfigOverride(resolved, request.ConfigOverride);
        }

        await foreach (var delta in _llmClient.ChatStreamAsync(
            request.WorkspaceId,
            request.SessionId,
            request.AgentTemplateId,
            request.Messages,
            request.Tools.Count > 0 ? request.Tools : null,
            resolved.Config,
            ct))
        {
            yield return delta;
        }
    }

    private static ResolvedLlmInvocationProfile ApplyConfigOverride(
        ResolvedLlmInvocationProfile resolved,
        LlmConfig? configOverride)
    {
        if (configOverride is null)
            return resolved;

        var merged = resolved.Config with
        {
            Endpoint = FirstNonBlank(configOverride.Endpoint, resolved.Config.Endpoint),
#pragma warning disable CS0618
            ApiKey = FirstNonBlank(configOverride.ApiKey, resolved.Config.ApiKey),
#pragma warning restore CS0618
            KeyVaultId = FirstNonBlank(configOverride.KeyVaultId, resolved.Config.KeyVaultId),
            ModelId = FirstNonBlank(configOverride.ModelId, resolved.Config.ModelId),
            ReasoningEffort = FirstNonBlank(configOverride.ReasoningEffort, resolved.Config.ReasoningEffort),
        };

        return resolved with
        {
            ModelId = merged.ModelId ?? resolved.ModelId,
            Config = merged,
        };
    }

    private static string? FirstNonBlank(string? first, string? fallback)
        => string.IsNullOrWhiteSpace(first) ? fallback : first;
}
