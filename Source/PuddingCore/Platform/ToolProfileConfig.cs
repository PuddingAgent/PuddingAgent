namespace PuddingCode.Platform;

/// <summary>
/// 工具配置文件：定义不同场景下应加载的工具子集。
/// </summary>
public static class ToolProfileConfig
{
    public const string HeartbeatProfileName = "heartbeat";
    public const string SubAgentProfileName = "sub_agent";

    /// <summary>
    /// 心跳自主执行工具集。这里只是对 Agent 已授权工具的二次筛选，不会绕过
    /// capability/approval；因此心跳可以推进工作，而不是只能诊断后等待用户。
    /// </summary>
    public static readonly HashSet<string> Heartbeat = new(StringComparer.OrdinalIgnoreCase)
    {
        "search_tools",
        "goal_read", "goal_update", "sleep", "receive_messages", "send_message",
        "agent_diagnostics", "agent_status", "search_memory", "save_memory",
        "save_preference", "query_sessions", "query_session_logs",
        "event_subscribe", "list_agents", "query_sub_agents", "spawn_sub_agent",
        "file_read", "list_dir", "file_search", "search_grep", "code_outline",
        "code_symbol_search", "code_summary", "project_map",
        "smart_explore", "smart_search", "smart_research", "smart_plan",
        "smart_develop", "smart_review", "smart_test", "smart_deploy",
        "file_write", "file_patch", "apply_patch", "shell",
        "terminal_start", "terminal_wait", "terminal_read", "terminal_status",
        "terminal_cancel", "terminal_input",
        "git_status", "git_diff", "git_log", "git_show", "git_commit", "git_push"
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
    /// D1 修复（选型 A「只增不减」）：heartbeat profile 是否仍应应用白名单裁剪。
    /// 已暴露集基准 = session committed toolset（append-only 记录/最新快照 ToolIds，非瞬时态/非逐轮重算）：
    /// <list type="bullet">
    ///   <item>已有已暴露工具集（非空）→ 返回 false：心跳 turn 不裁剪，暴露集与普通 turn 全量集一致，
    ///   消除 30↔24 tool_spec_changed 抖动（D1 根因，v12 事件）。</item>
    ///   <item>无已暴露工具集（全新 session）→ 返回 true：回退白名单过滤，保留该场景省 token 意图。</item>
    /// </list>
    /// 与 <c>ToolExposurePlanner</c> 的 append-only 防收缩语义对齐：心跳可在白名单内新增工具，
    /// 但不得删除任何已暴露工具。
    /// </summary>
    public static bool ShouldApplyHeartbeatToolFilter(IReadOnlySet<string>? exposedToolIds)
        => exposedToolIds is not { Count: > 0 };

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
