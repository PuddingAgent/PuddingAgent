using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingPlatform.Services.Agents;

namespace PuddingPlatform.Services.Configuration;

/// <summary>Trusted entry for Agent creation/archive: only an authenticated management subject is bound.</summary>
public sealed class AgentFactoryApiFactory(IAgentAuthoringBackend backend)
{
    public IAgentFactoryApi ForManagementUser(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new AgentDirectoryException("agent.forbidden", "A management login is required.");
        return new PlatformAgentFactoryApi(backend, subject);
    }
}
