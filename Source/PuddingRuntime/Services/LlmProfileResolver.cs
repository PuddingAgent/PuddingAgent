using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// LLM Profile 解析器 — 过渡实现：将 profile 解析为 direct provider 的 LlmConfig。
/// 后续配置目录治理完成后，从 workspace/agent 配置文件中读取 provider/profile/model。
/// </summary>
public sealed class LlmProfileResolver : ILlmProfileResolver
{
    private readonly ILogger<LlmProfileResolver> _logger;

    public LlmProfileResolver(ILogger<LlmProfileResolver> logger)
    {
        _logger = logger;
    }

    public Task<LlmConfig> ResolveAsync(
        string workspaceId,
        string agentInstanceId,
        LlmInvocationProfile profile,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[LlmProfileResolver] Resolve workspace={WorkspaceId} agent={AgentInstanceId} provider={ProviderId} profile={ProfileId} model={ModelId} role={Role}",
            workspaceId, agentInstanceId, profile.ProviderId, profile.ProfileId, profile.ModelId, profile.Role);

        // 过渡实现：所有请求走 direct provider，ModelId 直接传给 LlmConfig
        var llmConfig = new LlmConfig
        {
            ModelId = profile.ModelId,
        };

        return Task.FromResult(llmConfig);
    }
}
