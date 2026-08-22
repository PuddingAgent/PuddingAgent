using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PuddingCode.Security;
using PuddingPlatform.Services.Security;

namespace PuddingPlatformTests.Security;

/// <summary>
/// ADR-075 §15.1 scope/workspace 交叉矩阵与 scheme 隔离：
/// 仅 PuddingExternalAccessToken 身份可通过；scope/workspace AND 语义；ordinal 比较。
/// </summary>
[TestClass]
public sealed class ExternalAccessTokenAuthorizationHandlerTests
{
    private readonly ExternalAccessTokenAuthorizationHandler _handler = new();

    [TestMethod]
    public async Task ScopeClaimPresent_Succeeds()
    {
        var context = CreateContext(
            scopes: ["tasks.read"],
            workspaces: ["default"]);

        await _handler.HandleAsync(context);

        Assert.IsTrue(context.HasSucceeded);
    }

    [TestMethod]
    public async Task ScopeClaimAbsent_Denied()
    {
        // Token 只有 tasks.read，端点要求 tasks.command。
        var context = CreateContext(
            scopes: ["tasks.read"],
            workspaces: ["default"],
            requiredScope: ExternalTaskApiScopes.TasksCommand);

        await _handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded);
    }

    [TestMethod]
    public async Task JwtIdentity_WithForgedScopeClaim_Denied()
    {
        // 即使其他 scheme（如 JWT）伪造 pudding.scope claim，AuthenticationType 不匹配仍拒绝。
        var identity = new ClaimsIdentity(authenticationType: "Bearer");
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, ExternalTaskApiScopes.TasksRead));
        var user = new ClaimsPrincipal(identity);
        var requirement = new ExternalScopeRequirement(ExternalTaskApiScopes.TasksRead);
        var httpContext = new DefaultHttpContext();
        var context = new AuthorizationHandlerContext([requirement], user, httpContext);

        await _handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded);
    }

    [TestMethod]
    public async Task WorkspaceRouteValueMatchesClaim_Succeeds()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["workspaceId"] = "default";

        var context = CreateContext(
            scopes: ["tasks.read"],
            workspaces: ["default"],
            resource: httpContext,
            includeWorkspaceRequirement: true);

        await _handler.HandleAsync(context);

        Assert.IsTrue(context.HasSucceeded);
    }

    [TestMethod]
    public async Task WorkspaceRouteValueMismatch_Denied()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["workspaceId"] = "other-workspace";

        var context = CreateContext(
            scopes: ["tasks.read"],
            workspaces: ["default"],
            resource: httpContext,
            includeWorkspaceRequirement: true);

        await _handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded);
    }

    [TestMethod]
    public async Task WorkspaceMissingRouteValue_Denied()
    {
        var httpContext = new DefaultHttpContext();

        var context = CreateContext(
            scopes: ["tasks.read"],
            workspaces: ["default"],
            resource: httpContext,
            includeWorkspaceRequirement: true);

        await _handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded);
    }

    [TestMethod]
    public async Task WorkspaceComparison_IsOrdinal()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["workspaceId"] = "DEFAULT";

        var context = CreateContext(
            scopes: ["tasks.read"],
            workspaces: ["default"],
            resource: httpContext,
            includeWorkspaceRequirement: true);

        await _handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded, "workspaceId 比较必须 ordinal，大小写不同视为不匹配");
    }

    private static AuthorizationHandlerContext CreateContext(
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> workspaces,
        string? requiredScope = null,
        HttpContext? resource = null,
        bool includeWorkspaceRequirement = false)
    {
        var identity = new ClaimsIdentity(authenticationType: ExternalAccessTokenDefaults.Scheme);
        foreach (var scope in scopes)
            identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, scope));
        foreach (var workspace in workspaces)
            identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Workspace, workspace));
        var user = new ClaimsPrincipal(identity);

        var requirements = new List<IAuthorizationRequirement>();
        if (requiredScope is not null || !includeWorkspaceRequirement)
            requirements.Add(new ExternalScopeRequirement(requiredScope ?? ExternalTaskApiScopes.TasksRead));
        if (includeWorkspaceRequirement)
            requirements.Add(new ExternalWorkspaceRequirement());

        return new AuthorizationHandlerContext(
            requirements,
            user,
            resource ?? new DefaultHttpContext());
    }
}
