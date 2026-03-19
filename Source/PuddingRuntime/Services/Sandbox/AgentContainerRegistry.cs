using System.Collections.Concurrent;

namespace PuddingRuntime.Services.Sandbox;

/// <summary>
/// 内存中维护 Agent 实例 → Docker 容器的绑定台账。
/// 线程安全，面向 Singleton 注册。
/// </summary>
public sealed class AgentContainerRegistry
{
    private readonly ConcurrentDictionary<string, AgentContainerBinding> _byAgent = new();
    private readonly ConcurrentDictionary<string, AgentContainerBinding> _byContainer = new();

    /// <summary>注册或覆盖一个绑定记录。</summary>
    public void Register(AgentContainerBinding binding)
    {
        // 若同一 Agent 已有旧绑定，先移除旧容器索引
        if (_byAgent.TryGetValue(binding.AgentInstanceId, out var old))
            _byContainer.TryRemove(old.ContainerId, out _);

        _byAgent[binding.AgentInstanceId] = binding;
        _byContainer[binding.ContainerId] = binding;
    }

    public AgentContainerBinding? GetByAgent(string agentInstanceId) =>
        _byAgent.TryGetValue(agentInstanceId, out var b) ? b : null;

    public AgentContainerBinding? GetByContainer(string containerId) =>
        _byContainer.TryGetValue(containerId, out var b) ? b : null;

    public IReadOnlyList<AgentContainerBinding> GetByWorkspace(string workspaceId) =>
        _byAgent.Values.Where(b => b.WorkspaceId == workspaceId).ToList();

    public IReadOnlyList<AgentContainerBinding> GetAll() =>
        _byAgent.Values.OrderBy(b => b.CreatedAt).ToList();

    /// <summary>更新绑定状态（原地修改）。</summary>
    public void UpdateStatus(string agentInstanceId, AgentContainerStatus status, string? lastError = null)
    {
        if (!_byAgent.TryGetValue(agentInstanceId, out var b)) return;

        b.Status = status;
        if (lastError is not null) b.LastError = lastError;
        if (status is AgentContainerStatus.Stopped
                   or AgentContainerStatus.Removed
                   or AgentContainerStatus.Error)
            b.TerminatedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>从台账移除（通常在容器 Remove 后调用）。</summary>
    public bool Remove(string agentInstanceId)
    {
        if (!_byAgent.TryRemove(agentInstanceId, out var b)) return false;
        _byContainer.TryRemove(b.ContainerId, out _);
        return true;
    }
}
