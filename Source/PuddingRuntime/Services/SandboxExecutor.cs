using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 沙箱执行器——在调用任何工具/技能前，根据 AgentTemplate 的 CapabilityPolicy 进行门控检查。
/// 违规工具调用会被拦截并记录，不真正执行。
/// </summary>
public sealed class SandboxExecutor
{
    private readonly ILogger<SandboxExecutor> _logger;

    public SandboxExecutor(ILogger<SandboxExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 检查工具调用是否在 AgentTemplate 的 CapabilityPolicy 许可范围内。
    /// </summary>
    /// <param name="toolName">工具名称（如 "shell_execute"、"file_write"）。</param>
    /// <param name="capability">从 AgentTemplate 取得的能力策略，null 表示无策略（默认最小权限）。</param>
    /// <param name="agentInstanceId">用于日志追踪。</param>
    /// <returns>true 表示允许执行；false 表示被沙箱阻断。</returns>
    public bool IsAllowed(string toolName, CapabilityPolicy? capability, string agentInstanceId)
    {
        if (capability is null)
        {
            // 没有声明策略时，采用最小权限：不允许任何危险操作
            if (IsDangerousTool(toolName))
            {
                _logger.LogWarning("[Sandbox] Agent={Agent} attempted tool={Tool} but no CapabilityPolicy defined. Blocked.",
                    agentInstanceId, toolName);
                return false;
            }
            return true;
        }

        // 按名称白名单检查（优先）
        if (capability.AllowedToolNames.Count > 0
            && !capability.AllowedToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[Sandbox] Agent={Agent} tool={Tool} not in AllowedToolNames. Blocked.",
                agentInstanceId, toolName);
            return false;
        }

        // 按能力标志检查
        if (IsShellTool(toolName) && !capability.AllowShellExecution)
        {
            _logger.LogWarning("[Sandbox] Agent={Agent} tool={Tool} blocked: AllowShellExecution=false.",
                agentInstanceId, toolName);
            return false;
        }

        if (IsFileWriteTool(toolName) && !capability.AllowFileWrite)
        {
            _logger.LogWarning("[Sandbox] Agent={Agent} tool={Tool} blocked: AllowFileWrite=false.",
                agentInstanceId, toolName);
            return false;
        }

        if (IsNetworkTool(toolName) && !capability.AllowNetworkAccess)
        {
            _logger.LogWarning("[Sandbox] Agent={Agent} tool={Tool} blocked: AllowNetworkAccess=false.",
                agentInstanceId, toolName);
            return false;
        }

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

    // ── 工具分类辅助方法 ─────────────────────────────────────────────────────

    private static bool IsShellTool(string name) =>
        name.Equals("bash", StringComparison.OrdinalIgnoreCase)
        || name.Equals("python", StringComparison.OrdinalIgnoreCase)
        || name.Equals("read_file", StringComparison.OrdinalIgnoreCase)
        || name.Contains("shell", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cmd", StringComparison.OrdinalIgnoreCase)
        || name.Contains("exec", StringComparison.OrdinalIgnoreCase)
        || name.Contains("process", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileWriteTool(string name) =>
        name.Contains("write", StringComparison.OrdinalIgnoreCase)
        || name.Contains("delete", StringComparison.OrdinalIgnoreCase)
        || name.Contains("create_file", StringComparison.OrdinalIgnoreCase)
        || name.Contains("move", StringComparison.OrdinalIgnoreCase)
        || name.Contains("rename", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetworkTool(string name) =>
        name.Contains("http", StringComparison.OrdinalIgnoreCase)
        || name.Contains("fetch", StringComparison.OrdinalIgnoreCase)
        || name.Contains("request", StringComparison.OrdinalIgnoreCase)
        || name.Contains("download", StringComparison.OrdinalIgnoreCase)
        || name.Contains("upload", StringComparison.OrdinalIgnoreCase);

    private static bool IsDangerousTool(string name) =>
        IsShellTool(name) || IsFileWriteTool(name) || IsNetworkTool(name);
}
