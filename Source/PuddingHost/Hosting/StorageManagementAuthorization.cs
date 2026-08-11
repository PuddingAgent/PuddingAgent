using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using PuddingBrowser.Protocol;

namespace PuddingHost.Hosting;

public static class PuddingAuthorizationPolicies
{
    public const string StorageManagement = "StorageManagement";
}

public sealed class StorageManagementRequirement : IAuthorizationRequirement;

/// <summary>
/// Storage management is available to an authenticated platform admin, or to
/// the product Desktop over loopback with its rotating control token. The
/// Desktop-token path is disabled for non-Desktop hosts.
/// </summary>
public sealed class StorageManagementAuthorizationHandler(
    PuddingHostOptions hostOptions,
    DesktopControlTokenValidator tokenValidator)
    : AuthorizationHandler<StorageManagementRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StorageManagementRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.IsInRole("admin"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (hostOptions.Mode != PuddingHostMode.DesktopChild
            || context.Resource is not HttpContext httpContext
            || !IsLoopback(httpContext.Connection.RemoteIpAddress))
        {
            return Task.CompletedTask;
        }

        var presentedToken = httpContext.Request.Headers[
            BrowserBridgeProtocol.ControlTokenHeader].FirstOrDefault();
        if (tokenValidator.Validate(presentedToken))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    private static bool IsLoopback(IPAddress? address)
        => address is not null && IPAddress.IsLoopback(address);
}
