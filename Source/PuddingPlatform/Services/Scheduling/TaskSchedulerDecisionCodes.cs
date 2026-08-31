namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 调度决策统一码字典（gapmap R6）。所有落库 decision_code 必须是 snake_case 稳定常量，
/// 供跨重启对账、shadow 对账与 Nightly report 下钻。既有写点（TaskGoalDispatchTransactionStore、
/// TaskBacklogRefinementStore、TaskCommandService、TaskExecutionRepairCoordinator）的自由字符串
/// code 保持兼容，通过 <see cref="Normalize"/> 映射；字典外未知 code 小写化透传，不丢弃。
/// </summary>
public static class TaskSchedulerDecisionCodes
{
    // ── candidate 阶段（Evaluator 输出）───────────────────────
    public const string Eligible = "eligible";
    public const string TaskNotYetEligible = "task_not_yet_eligible";
    public const string TaskDependencyWaiting = "task_dependency_waiting";
    public const string TaskDependencyBroken = "task_dependency_broken";
    public const string PreferredAgentUnavailableOrIncompatible = "preferred_agent_unavailable_or_incompatible";
    public const string NoCompatibleAgent = "no_compatible_agent";
    public const string AgentNotIdle = "agent_not_idle";
    public const string AgentIdleGracePeriod = "agent_idle_grace_period";
    public const string ExecutionWindowUnknown = "execution_window_unknown";
    public const string ExecutionWindowClosed = "execution_window_closed";
    public const string AgentAlreadySelectedThisScan = "agent_already_selected_this_scan";

    // ── refinement 阶段（Backlog verdict，稳定 snake_case）────
    public const string ReadyForAutoDispatch = "ready_for_auto_dispatch";
    public const string DescriptionRequired = "description_required";
    public const string AcceptanceCriteriaRequired = "acceptance_criteria_required";
    public const string TaskTypeUnclassified = "task_type_unclassified";

    // ── 既有写点码（保持兼容，不强行改写）─────────────────────
    public const string BacklogRefined = "backlog_refined";

    /// <summary>
    /// 自由字符串 code → 统一 snake_case。已登记别名精确映射；
    /// 未知 code 兜底 Trim + 小写 + 空白转下划线，绝不返回空串。
    /// </summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "unknown";
        var trimmed = code.Trim();
        var normalized = Known.TryGetValue(trimmed, out var mapped)
            ? mapped
            : trimmed.ToLowerInvariant().Replace(' ', '_');
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    /// <summary>PascalCase / camelCase 别名 → snake_case 稳定码。</summary>
    private static readonly IReadOnlyDictionary<string, string> Known =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TaskNotYetEligible"] = TaskNotYetEligible,
            ["TaskDependencyWaiting"] = TaskDependencyWaiting,
            ["TaskDependencyBroken"] = TaskDependencyBroken,
            ["PreferredAgentUnavailableOrIncompatible"] = PreferredAgentUnavailableOrIncompatible,
            ["NoCompatibleAgent"] = NoCompatibleAgent,
            ["AgentNotIdle"] = AgentNotIdle,
            ["AgentIdleGracePeriod"] = AgentIdleGracePeriod,
            ["ExecutionWindowUnknown"] = ExecutionWindowUnknown,
            ["ExecutionWindowClosed"] = ExecutionWindowClosed,
            ["AgentAlreadySelectedThisScan"] = AgentAlreadySelectedThisScan,
            ["ReadyForAutoDispatch"] = ReadyForAutoDispatch,
            ["DescriptionRequired"] = DescriptionRequired,
            ["AcceptanceCriteriaRequired"] = AcceptanceCriteriaRequired,
            ["TaskTypeUnclassified"] = TaskTypeUnclassified,
            ["BacklogRefined"] = BacklogRefined,
            ["Eligible"] = Eligible,
        };
}
