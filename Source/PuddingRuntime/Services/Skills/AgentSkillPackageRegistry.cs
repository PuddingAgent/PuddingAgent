using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// 运行时 Skill 包注册表（单例）—— agentInstanceId → Skill 包列表。
/// AgentExecutionService 在任务开始时注册，结束时注销。
/// ContainerSkillBase 在启动容器前查询，以决定是否需要下载并挂载 Skill 包。
/// </summary>
public sealed class AgentSkillPackageRegistry
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<SkillPackageInfo>> _store = new();

    public void Register(string agentInstanceId, IReadOnlyList<SkillPackageInfo> packages)
        => _store[agentInstanceId] = packages;

    public IReadOnlyList<SkillPackageInfo> Get(string agentInstanceId)
        => _store.TryGetValue(agentInstanceId, out var pkgs) ? pkgs : [];

    public void Remove(string agentInstanceId)
        => _store.TryRemove(agentInstanceId, out _);
}
