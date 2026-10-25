using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 把 <see cref="IAgentReasoningEffortStore"/> 落到 Agent 实例 manifest 上。
///
/// 真源是实例 manifest：聊天执行路由（AgentRuntimeProfileResolver）只读它；
/// llm.json 是管理端兼容镜像，由 <see cref="WorkspaceAgentFileService"/> 负责同步。
/// 因此这里刻意不直接碰 llm.json，避免出现第二真源。
/// </summary>
public sealed class WorkspaceAgentReasoningEffortStore(WorkspaceAgentFileService agents)
    : IAgentReasoningEffortStore
{
    public async Task<string?> GetReasoningEffortAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        var agent = await agents.GetAgentAsync(workspaceId, agentId, ct)
            ?? throw new KeyNotFoundException(
                $"Agent '{agentId}' in workspace '{workspaceId}' 不存在");
        return Normalize(agent.ReasoningEffort);
    }

    public async Task<string?> SetReasoningEffortAsync(
        string workspaceId,
        string agentId,
        string? effort,
        CancellationToken ct = default)
    {
        // 合法性由 UpdateReasoningEffortAsync 内部的 ReasoningEffortPolicy 统一裁决，
        // 这一层不再重复判断，避免两处规则漂移（写入口有两个：这里与 HTTP 窄端点）。
        var updated = await agents.UpdateReasoningEffortAsync(workspaceId, agentId, effort, ct);
        return Normalize(updated.ReasoningEffort);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
