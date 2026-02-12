using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 沙箱执行器保留为运行期二级门控入口。
/// 当前工具可见性与高风险授权由 ToolPermissionPolicyService 和执行服务统一处理；
/// 这里仅保留统一的审计日志入口，避免旧版启发式分类散落在执行链路中。
/// </summary>
public sealed class SandboxExecutor
{
    private readonly ILogger<SandboxExecutor> _logger;

    public SandboxExecutor(ILogger<SandboxExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 执行服务已经完成策略门控；此处只记录二级门控审计点。
    /// </summary>
    public bool IsAllowed(string toolName, CapabilityPolicy? capability, string agentInstanceId)
    {
        _logger.LogDebug(
            "[Sandbox] AuditPass tool={Tool} agent={Agent} policy={Policy}",
            toolName,
            agentInstanceId,
            capability is null ? "none" : "present");
        return true;
    }

    /// <summary>
    /// 批量检查工具列表，返回被阻止的工具名列表（空列表表示全部允许）。
    /// </summary>
    public IReadOnlyList<string> GetBlockedTools(
        IEnumerable<string> toolNames,
        CapabilityPolicy? capability,
        string agentInstanceId)
    {
        return toolNames
            .Where(t => !IsAllowed(t, capability, agentInstanceId))
            .ToArray();
    }
}
