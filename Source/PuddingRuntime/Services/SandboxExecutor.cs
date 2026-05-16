using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 沙箱执行器——在调用任何工具/技能前，根据 AgentTemplate 的 CapabilityPolicy 进行门控检查。
/// 
/// V1.0 状态：沙箱旁路。IsAllowed() 始终返回 true。
/// 接口和数据模型保留，V2 重新接入时只需改回此文件。
/// </summary>
public sealed class SandboxExecutor
{
    private readonly ILogger<SandboxExecutor> _logger;

    public SandboxExecutor(ILogger<SandboxExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// V1.0 沙箱旁路：始终允许。
    /// 检查逻辑保留在注释中，V2 恢复时取消注释即可。
    /// </summary>
    public bool IsAllowed(string toolName, CapabilityPolicy? capability, string agentInstanceId)
    {
        _logger.LogDebug("[Sandbox] V1.0 bypass: tool={Tool} agent={Agent}", toolName, agentInstanceId);
        return true;

        /* ── V2 恢复区 ──────────────────────────────────────────────
        if (capability is null)
        {
            if (IsDangerousTool(toolName))
            {
                _logger.LogWarning("[Sandbox] Agent={Agent} tool={Tool} no CapabilityPolicy. Blocked.",
                    agentInstanceId, toolName);
                return false;
            }
            return true;
        }

        if (capability.AllowedToolNames.Count > 0
            && !capability.AllowedToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase)
            && !capability.DefaultToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase)
            && !capability.RequiresGrantToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[Sandbox] Agent={Agent} tool={Tool} not in capability. Blocked.",
                agentInstanceId, toolName);
            return false;
        }

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
        ────────────────────────────────────────────────────────────*/
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
