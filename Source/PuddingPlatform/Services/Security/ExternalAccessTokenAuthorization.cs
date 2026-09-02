using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PuddingCode.Security;

namespace PuddingPlatform.Services.Security;

/// <summary>ADR-075/082 冻结的 Policy 名称（Host 组合根与 Controller 共用）。</summary>
public static class ExternalAccessTokenPolicyNames
{
    /// <summary>Token 管理：JWT scheme + admin role。</summary>
    public const string AdminAccessTokenManagement = "AdminAccessTokenManagement";

    /// <summary>External API 已认证（whoami 等无 scope 端点）。</summary>
    public const string ExternalApiAuthenticated = "ExternalApiAuthenticated";

    public const string ExternalTasksRead = "ExternalTasksRead";
    public const string ExternalTasksWrite = "ExternalTasksWrite";
    public const string ExternalTasksComment = "ExternalTasksComment";
    public const string ExternalTasksEvaluate = "ExternalTasksEvaluate";
    public const string ExternalTasksCommand = "ExternalTasksCommand";
    public const string ExternalWorkspacesRead = "ExternalWorkspacesRead";
    public const string ExternalWorkspaceRead = "ExternalWorkspaceRead";
    public const string ExternalAgentsRead = "ExternalAgentsRead";
    public const string ExternalMessagesSend = "ExternalMessagesSend";
}

/// <summary>要求 principal 拥有指定 scope（AND 语义，无通配符）。</summary>
public sealed class ExternalScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

/// <summary>要求 route value workspaceId 位于 Token workspace allow-list（ordinal 比较）。</summary>
public sealed class ExternalWorkspaceRequirement : IAuthorizationRequirement;

/// <summary>
/// ADR-075: scope/workspace Requirement 的 AuthorizationHandler。
/// Policy 已显式选择 PuddingExternalAccessToken scheme；此处再校验 identity 确实来自
/// 该 scheme，防止未来同名 claim 从其他 scheme 混入。
/// </summary>
public sealed class ExternalAccessTokenAuthorizationHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case ExternalScopeRequirement scopeRequirement:
                    HandleScope(context, scopeRequirement);
                    break;
                case ExternalWorkspaceRequirement workspaceRequirement:
                    HandleWorkspace(context, workspaceRequirement);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private static void HandleScope(AuthorizationHandlerContext context, ExternalScopeRequirement requirement)
    {
        if (IsExternalTokenPrincipal(context)
            && context.User.HasClaim(c =>
                c.Type == ExternalAccessTokenClaimNames.Scope
                && string.Equals(c.Value, requirement.Scope, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }
    }

    private static void HandleWorkspace(
        AuthorizationHandlerContext context,
        ExternalWorkspaceRequirement requirement)
    {
        if (!IsExternalTokenPrincipal(context))
            return;

        if (context.Resource is not HttpContext httpContext)
            return;

        var routeWorkspaceId = httpContext.GetRouteValue("workspaceId") as string;
        if (string.IsNullOrEmpty(routeWorkspaceId))
            return;

        if (context.User.HasClaim(c =>
                c.Type == ExternalAccessTokenClaimNames.Workspace
                && string.Equals(c.Value, routeWorkspaceId, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }
    }

    private static bool IsExternalTokenPrincipal(AuthorizationHandlerContext context)
        => context.User.Identity?.IsAuthenticated == true
            && string.Equals(
                context.User.Identity.AuthenticationType,
                ExternalAccessTokenDefaults.Scheme,
                StringComparison.Ordinal);
}
