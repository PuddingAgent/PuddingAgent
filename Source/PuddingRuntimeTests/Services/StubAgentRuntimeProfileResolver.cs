using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 运行时档案解析桩：只为压缩路由测试提供 provider/model 身份，不读数据库、不读配置。
/// AgentRuntimeProfile 的必填项只有 WorkspaceId / AgentId / DisplayName。
/// </summary>
internal sealed class StubAgentRuntimeProfileResolver : IAgentRuntimeProfileResolver
{
    private readonly string _providerId;
    private readonly string _modelId;

    public StubAgentRuntimeProfileResolver(string providerId = "stub-provider", string modelId = "stub-model")
    {
        _providerId = providerId;
        _modelId = modelId;
    }

    public Task<AgentRuntimeProfile> ResolveAsync(string workspaceId, string agentId, CancellationToken ct = default)
        => Task.FromResult(new AgentRuntimeProfile
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
            DisplayName = agentId,
            PreferredProviderId = _providerId,
            PreferredModelId = _modelId,
            // 真实解析器会把已解析的消费配置一并给出；压缩的 dispatch 需要它（下游对 null 会抛错）。
            LlmConfig = new LlmConfig { ModelId = _modelId, Endpoint = "https://stub.invalid/v1" },
        });
}
