using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Configuration;

/// <summary>
/// Trusted entry: only a management login may obtain the bound policy API; the caller identity is the authenticated
/// subject, never a request-body ActorId or AgentId. External tokens/plugins need their own grant adapter.
/// </summary>
public sealed class AgentModelPolicyApiFactory(IAgentModelPolicyStore store)
{
    public IAgentModelPolicyApi ForManagementUser(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new AgentModelPolicyException(ModelErrorCodes.ScopeForbidden, "A management login is required.");
        return new PlatformAgentModelPolicyApi(store, subject);
    }
}
