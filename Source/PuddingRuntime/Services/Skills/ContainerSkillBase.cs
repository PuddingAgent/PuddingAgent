using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// 依赖 Docker 容器沙箱的 Agent Skill 基类。
/// 提供按需容器启动逻辑，避免在各容器型 Skill 中重复代码。
/// </summary>
public abstract class ContainerSkillBase : IAgentSkill
{
    protected readonly AgentContainerRegistry Registry;
    protected readonly ISandboxProvider Sandbox;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly ILogger _logger;

    protected ContainerSkillBase(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger logger)
    {
        Registry = registry;
        Sandbox  = sandbox;
        _skillPackageRegistry = skillPackageRegistry;
        _logger  = logger;
    }

    public abstract string SkillId { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool RequiresShellExecution { get; }

    public abstract Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default);

    /// <summary>
    /// 确保 Agent 的沙箱容器在运行状态；不存在时自动按需创建。
    /// 若 Agent 关联了 Skill 包，会将 pudding_skills 卷以只读方式挂载到 /skills/。
    /// </summary>
    protected async Task<AgentContainerBinding?> EnsureContainerRunningAsync(
        string agentInstanceId, string workspaceId, CancellationToken ct)
    {
        var binding = Registry.GetByAgent(agentInstanceId);

        if (binding is { Status: AgentContainerStatus.Running })
        {
            if (await Sandbox.IsRunningAsync(binding.ContainerId, ct))
                return binding;

            Registry.UpdateStatus(agentInstanceId, AgentContainerStatus.Error, "Container exited unexpectedly");
        }

        _logger.LogInformation("[{Skill}] Auto-provisioning container for agent={Agent}",
            GetType().Name, agentInstanceId);

        // 若有 Skill 包，挂载 pudding_skills 卷到 /skills（只读）
        var skillPackages = _skillPackageRegistry.Get(agentInstanceId);
        var mounts = skillPackages.Count > 0
            ? (IReadOnlyList<string>)["pudding_skills:/skills:ro"]
            : [];

        var result = await Sandbox.StartAsync(new SandboxStartRequest
        {
            AgentInstanceId = agentInstanceId,
            WorkspaceId     = workspaceId,
            Image           = "ubuntu:22.04",
            Mounts          = mounts,
        }, ct);

        if (!result.Success)
        {
            _logger.LogError("[{Skill}] Failed to start container for agent={Agent}: {Err}",
                GetType().Name, agentInstanceId, result.Error);
            return null;
        }

        var newBinding = new AgentContainerBinding
        {
            AgentInstanceId = agentInstanceId,
            WorkspaceId     = workspaceId,
            ContainerId     = result.ContainerId!,
            ContainerName   = result.ContainerName!,
            Image           = "ubuntu:22.04",
            Status          = AgentContainerStatus.Running,
            SkillPackages   = skillPackages,
        };
        Registry.Register(newBinding);
        return newBinding;
    }

    protected static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };
}
