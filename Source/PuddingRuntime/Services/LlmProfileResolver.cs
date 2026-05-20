using Microsoft.Extensions.Logging;
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

    public LlmProfileResolver(ILogger<LlmProfileResolver> logger)
    {
        _logger = logger;
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

        // 过渡实现：所有请求走 direct provider，完整记录 provider/profile/model/role
        var llmConfig = new LlmConfig
        {
            ModelId = profile.ModelId,
        };

        var resolved = new ResolvedLlmInvocationProfile
        {
            ProviderId = profile.ProviderId,
            ProfileId = profile.ProfileId,
            ModelId = profile.ModelId,
            Role = profile.Role,
            Config = llmConfig,
            Metadata = new Dictionary<string, string>
            {
                ["provider_id"] = profile.ProviderId,
                ["profile_id"] = profile.ProfileId,
                ["model_id"] = profile.ModelId,
                ["role"] = profile.Role,
                ["resolver"] = "legacy.direct",
            },
        };

        return Task.FromResult(resolved);
    }
}
