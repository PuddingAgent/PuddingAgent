namespace PuddingCode.Platform;

/// <summary>
/// 工具配置文件：定义不同场景下应加载的工具子集。
/// </summary>
public static class ToolProfileConfig
{
    public const string HeartbeatProfileName = "heartbeat";
    public const string SubAgentProfileName = "sub_agent";

    /// <summary>心跳/维护场景的最小工具集</summary>
    public static readonly HashSet<string> Heartbeat = new(StringComparer.OrdinalIgnoreCase)
    {
        "goal_read", "goal_update", "sleep", "receive_messages", "send_message",
        "agent_diagnostics", "agent_status", "search_memory", "save_memory",
        "query_session_logs", "manage_tasks", "file_read", "list_dir"
    };

    /// <summary>子代理的最小工具集</summary>
    public static readonly HashSet<string> SubAgent = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_read", "file_write", "file_patch", "list_dir", "file_search",
        "search_grep", "code_outline", "code_symbol_search", "code_summary",
        "terminal_start", "terminal_wait", "terminal_read", "terminal_status",
        "shell", "spawn_sub_agent", "send_message"
    };

    /// <summary>判断给定工具集是否应该包含指定工具</summary>
    public static bool ShouldInclude(string profileName, string toolId)
    {
        return profileName switch
        {
            HeartbeatProfileName => Heartbeat.Contains(toolId),
            SubAgentProfileName => SubAgent.Contains(toolId),
            _ => true // 默认包含所有工具
        };
    }

    /// <summary>
    /// 根据可信运行时元数据选择工具配置。用户消息文本不参与系统场景识别，
    /// 避免普通消息伪造心跳标记。显式工具选择优先于子代理兜底配置。
    /// </summary>
    public static string? ResolveProfile(
        RuntimeDispatchRequest request,
        CapabilityPolicy? capability = null,
        AgentTemplateDefinition? template = null)
    {
        if (request.Origin is
            {
                FromKind: var fromKind,
                FromId: var fromId,
            }
            && string.Equals(fromKind, PuddingCode.Models.MessageEndpointKinds.System, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fromId, "heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            return HeartbeatProfileName;
        }

        if (request.ExecutionIdentity?.Kind != PuddingCode.Runtime.RuntimeExecutionKind.SubAgent)
            return null;

        if (HasExplicitToolSelection(capability, template))
            return null;

        return SubAgentProfileName;
    }

    private static bool HasExplicitToolSelection(
        CapabilityPolicy? capability,
        AgentTemplateDefinition? template)
        => capability?.AllowedToolNames is { Count: > 0 }
           || capability?.DefaultToolNames is { Count: > 0 }
           || capability?.RequiresGrantToolNames is { Count: > 0 }
           || template?.AllowedSkillIds is { Count: > 0 };
}
