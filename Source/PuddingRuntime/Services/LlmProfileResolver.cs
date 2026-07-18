using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// LLM Profile 解析器 — 将 provider/profile/model/role 解析为完整配置。
/// 过渡实现：所有请求走 direct provider，但 provider/profile/model/role 全部记录。
/// 后续配置目录治理完成后，从 workspace/agent 配置文件中读取真实 provider/profile。
/// </summary>
public sealed class LlmProfileResolver : ILlmProfileResolver
{
    private readonly ILogger<LlmProfileResolver> _logger;
    private readonly ILlmConfigService? _llmConfigService;

    public LlmProfileResolver(
        ILogger<LlmProfileResolver> logger,
        ILlmConfigService? llmConfigService = null)
    {
        _logger = logger;
        _llmConfigService = llmConfigService;
    }

    public Task<ResolvedLlmInvocationProfile> ResolveAsync(
        string workspaceId,
        string agentInstanceId,
        LlmInvocationProfile profile,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[LlmProfileResolver] Resolve workspace={WorkspaceId} agent={AgentInstanceId} provider={ProviderId} profile={ProfileId} model={ModelId} role={Role}",
            workspaceId, agentInstanceId, profile.ProviderId, profile.ProfileId, profile.ModelId, profile.Role);

        var providerId = profile.ProviderId;
        var profileId = profile.ProfileId;
        var modelId = profile.ModelId;
        LlmConfig? llmConfig = null;
        var resolverName = "llm-config.fallback";

        if (_llmConfigService is not null)
        {
            var resolvedProfile = _llmConfigService.ResolveProfile(profile.ProfileId);
            if (resolvedProfile is not null)
            {
                providerId = resolvedProfile.ProviderId;
                profileId = resolvedProfile.ProfileId;
                modelId = resolvedProfile.ModelId;
                llmConfig = resolvedProfile.Config;
                resolverName = "llm-config.profile";
            }
            else
            {
                llmConfig = _llmConfigService.Resolve(profile.ProviderId, profile.ModelId);
                if (llmConfig is not null)
                {
                    modelId = llmConfig.ModelId ?? profile.ModelId;
                    resolverName = "llm-config.provider-model";
                }
            }
        }

        // ADR-058: No legacy.direct fallback. Provider must be resolvable.
        if (llmConfig is null)
        {
            throw new InvalidOperationException(
                $"LLM profile cannot be resolved for agent '{agentInstanceId}': " +
                $"provider={providerId ?? "(null)"} model={modelId ?? "(null)"} profile={profileId ?? "(null)"}. " +
                "Verify the provider/model/profile exists in data/config/llm.providers.json.");
        }

        var resolved = new ResolvedLlmInvocationProfile
        {
            ProviderId = providerId,
            ProfileId = profileId,
            ModelId = modelId,
            Role = profile.Role,
            Config = llmConfig,
            Metadata = new Dictionary<string, string>
            {
                ["provider_id"] = providerId,
                ["profile_id"] = profileId,
                ["model_id"] = modelId,
                ["role"] = profile.Role,
                ["resolver"] = resolverName,
            },
        };

        return Task.FromResult(resolved);
    }
}
