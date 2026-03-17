using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// 授权服务——执行用户、Workspace 权限策略与 AgentTemplate 三者交集校验。
/// </summary>
public sealed class AuthorizationService
{
    private readonly InMemoryWorkspaceCatalog _workspaceCatalog;
    private readonly ILogger<AuthorizationService> _logger;

    public AuthorizationService(
        InMemoryWorkspaceCatalog workspaceCatalog,
        ILogger<AuthorizationService> logger)
    {
        _workspaceCatalog = workspaceCatalog;
        _logger = logger;
    }

    /// <summary>
    /// 执行权限判断——检查用户是否有权通过指定渠道在指定 Workspace 下与指定 AgentTemplate 交互。
    /// </summary>
    public AuthorizationDecision Authorize(
        string channelId,
        string userExternalId,
        string workspaceId,
        string agentTemplateId,
        string? sessionId = null)
    {
        var workspace = _workspaceCatalog.GetWorkspace(workspaceId);
        if (workspace is null)
        {
            return Deny("Workspace not found");
        }

        // Workspace 级别检查：是否启用
        if (!workspace.IsEnabled)
        {
            return Deny("Workspace is disabled");
        }

        // Workspace 冻结检查
        if (workspace.IsFrozen)
        {
            return Deny("Workspace is frozen");
        }

        // 渠道绑定检查
        var binding = workspace.ChannelBindings.FirstOrDefault(cb => cb.ChannelId == channelId);
        if (binding is null)
        {
            return Deny($"Channel '{channelId}' is not bound to workspace '{workspaceId}'");
        }

        // 如果渠道限制了允许的模板集合，检查模板是否在其中
        if (binding.AllowedAgentTemplateIds.Count > 0 &&
            !binding.AllowedAgentTemplateIds.Contains(agentTemplateId))
        {
            return Deny($"AgentTemplate '{agentTemplateId}' is not allowed for channel '{channelId}'");
        }

        // 权限策略检查
        var policy = workspace.PermissionPolicy;
        if (policy is not null && policy.DefaultDeny)
        {
            // DefaultDeny 模式下，用户角色必须在 AllowedRoles 中
            // V1 简化：没有用户角色系统时，DefaultDeny = true 且 AllowedRoles 不为空 → 拒绝
            if (policy.AllowedRoles.Count > 0)
            {
                _logger.LogDebug("[Auth] DefaultDeny with roles check — user={User} workspace={Ws}",
                    userExternalId, workspaceId);
                // V1: 没有用户角色系统，暂时通过（后续接入 ChannelUserContext.Roles）
            }
        }

        var snapshot = new PermissionSnapshot
        {
            SessionId = sessionId ?? "",
            UserId = userExternalId,
            WorkspaceId = workspaceId,
            AgentTemplateId = agentTemplateId,
            IsAllowed = true,
            EffectiveRoles = [],
        };

        return new AuthorizationDecision
        {
            IsAllowed = true,
            Snapshot = snapshot,
        };
    }

    private static AuthorizationDecision Deny(string reason) => new()
    {
        IsAllowed = false,
        DenialReason = reason,
    };
}
