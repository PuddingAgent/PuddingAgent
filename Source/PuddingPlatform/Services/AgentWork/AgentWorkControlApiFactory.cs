using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;
using PuddingCode.Security;

namespace PuddingPlatform.Services.AgentWork;

/// <summary>PA-02 management entry for Work Observe/Cancel; the actor and addressed Agent are host-bound.</summary>
public sealed class AgentWorkControlApiFactory(IAgentWorkControlBackend backend)
{
    public IAgentWorkControlApi ForManagementUser(ClaimsPrincipal principal, AgentRef scope)
    {
        var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(actor) || actor.Length > 64
            || actor.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new AgentWorkControlException("permission.denied", "A management login is required.");
        return new PlatformAgentWorkControlApi(backend, new WorkCaller(actor), scope);
    }
}

public sealed record WorkCancelBackendResult(string Effect, long Revision, string? CurrentStatus);
