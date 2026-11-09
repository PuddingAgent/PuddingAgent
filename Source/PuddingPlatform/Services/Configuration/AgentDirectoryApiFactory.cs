using System.Security.Claims;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingPlatform.Services.Agents;

namespace PuddingPlatform.Services.Configuration;

/// <summary>Trusted entry for the directory: only an authenticated management subject is bound as actor.</summary>
public sealed class AgentDirectoryApiFactory(IAgentDirectoryStore store)
{
    public IAgentDirectoryApi ForManagementUser(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new AgentDirectoryException(AgentErrorCodes.Forbidden, "A management login is required.");
        return new PlatformAgentDirectoryApi(store, subject);
    }
}
