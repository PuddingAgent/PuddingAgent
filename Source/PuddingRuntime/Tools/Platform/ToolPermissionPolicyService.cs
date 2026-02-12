using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 默认 Tool 权限策略。
/// 该服务是 Tool 风险分类、模板暴露和运行时授权判断的单一入口。
/// </summary>
public sealed class ToolPermissionPolicyService : IToolPermissionPolicyService
{
    private readonly IRuntimeControlService? _runtimeControl;

    public ToolPermissionPolicyService(IRuntimeControlService? runtimeControl = null)
    {
        _runtimeControl = runtimeControl;
    }

    public ToolPermissionDecision Classify(ToolDescriptor descriptor)
    {
        var requiresShell = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell);
        var requiresFileWrite = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
                                || descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive);
        var requiresNetwork = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork);
        var requiresRuntimeAuthorization = RequiresRuntimeAuthorization(descriptor);
        var isLowRiskControlPlaneTool = descriptor.PermissionLevel == ToolPermissionLevel.Low
                                        && descriptor.Category == ToolCategory.Security;
        var isLowRiskReadOnlyTool = descriptor.PermissionLevel == ToolPermissionLevel.Low
                                    || descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly);
        var isLowRiskAgentCoordinationTool = IsLowRiskAgentCoordinationTool(descriptor);
        var isLowRiskAgentPrivateSkillTool = IsLowRiskAgentPrivateSkillTool(descriptor);
        var tier = requiresRuntimeAuthorization
            ? ToolPermissionTier.RuntimeGranted
            : isLowRiskReadOnlyTool
              || isLowRiskControlPlaneTool
              || isLowRiskAgentCoordinationTool
              || isLowRiskAgentPrivateSkillTool
                ? ToolPermissionTier.AutoAllowed
                : ToolPermissionTier.TemplateGranted;

        return new ToolPermissionDecision
        {
            ToolId = descriptor.ToolId,
            Tier = tier,
            IsExposedToAgent = tier != ToolPermissionTier.Blocked,
            RequiresRuntimeAuthorization = requiresRuntimeAuthorization,
            RequiresShellExecution = requiresShell,
            RequiresFileWrite = requiresFileWrite,
            RequiresNetworkAccess = requiresNetwork,
            Reason = tier switch
            {
                ToolPermissionTier.AutoAllowed => isLowRiskControlPlaneTool
                    ? "low-risk control-plane security tool"
                    : isLowRiskAgentCoordinationTool
                        ? "low-risk agent coordination tool"
                        : isLowRiskAgentPrivateSkillTool
                            ? "low-risk agent-private SKILL tool"
                            : "low-risk read-only tool",
                ToolPermissionTier.TemplateGranted => "template-granted tool",
                ToolPermissionTier.RuntimeGranted => "runtime authorization required",
                _ => "blocked",
            },
        };
    }

    public bool RequiresRuntimeAuthorization(ToolDescriptor descriptor)
        => descriptor.PermissionLevel == ToolPermissionLevel.High
           || descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell)
           || descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
           || descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive);

    private static bool IsLowRiskAgentCoordinationTool(ToolDescriptor descriptor)
    {
        if (descriptor.PermissionLevel != ToolPermissionLevel.Low)
            return false;

        if (descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell)
            || descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
            || descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork)
            || descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive))
        {
            return false;
        }

        return descriptor.ToolId.Equals("spawn_sub_agent", StringComparison.OrdinalIgnoreCase)
               || descriptor.ToolId.Equals("send_message", StringComparison.OrdinalIgnoreCase)
               || descriptor.ToolId.Equals("receive_messages", StringComparison.OrdinalIgnoreCase)
               || descriptor.ToolId.Equals("sleep", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowRiskAgentPrivateSkillTool(ToolDescriptor descriptor)
    {
        if (descriptor.PermissionLevel != ToolPermissionLevel.Low)
            return false;

        if (descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell)
            || descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
            || descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork)
            || descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive))
        {
            return false;
        }

        return descriptor.ToolId.Equals("agent_skill", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanExposeToAgent(ToolDescriptor descriptor, CapabilityPolicy? policy)
    {
        // YOLO 模式：绕过所有工具权限检查
        if (_runtimeControl?.Mode == RuntimeExecutionMode.Yolo)
            return true;

        var decision = Classify(descriptor);
        if (decision.Tier == ToolPermissionTier.AutoAllowed)
            return true;

        // 没有策略时的兜底——非 Blocked 工具默认可用
        if (policy is null)
            return decision.Tier != ToolPermissionTier.Blocked;

        if (policy.AllowedToolNames.Count > 0)
        {
            return policy.AllowedToolNames.Contains(descriptor.ToolId, StringComparer.OrdinalIgnoreCase);
        }

        return decision.Tier == ToolPermissionTier.RuntimeGranted
            ? policy.RequiresGrantToolNames.Contains(descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            : policy.GetAllEffectiveToolNames().Contains(descriptor.ToolId);
    }

    public CapabilityPolicy BuildCapabilityPolicy(
        IEnumerable<ToolDescriptor> descriptors,
        IEnumerable<string> selectedToolNames,
        bool isTaskRole)
    {
        var selected = selectedToolNames
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var defaultTools = new List<string>();
        var grantTools = new List<string>();
        var allowShell = false;
        var allowFileWrite = false;
        var allowNetwork = false;

        foreach (var descriptor in descriptors)
        {
            if (!selected.Contains(descriptor.ToolId))
                continue;

            var decision = Classify(descriptor);
            if (decision.RequiresRuntimeAuthorization)
                grantTools.Add(descriptor.ToolId);
            else
                defaultTools.Add(descriptor.ToolId);

            allowShell |= decision.RequiresShellExecution;
            allowFileWrite |= decision.RequiresFileWrite;
            allowNetwork |= decision.RequiresNetworkAccess;
        }

        return new CapabilityPolicy
        {
            AllowShellExecution = allowShell,
            AllowFileWrite = allowFileWrite,
            AllowNetworkAccess = allowNetwork,
            DefaultToolNames = defaultTools
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RequiresGrantToolNames = grantTools
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AllowedToolNames = isTaskRole
                ? defaultTools.Concat(grantTools)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [],
        };
    }
}
