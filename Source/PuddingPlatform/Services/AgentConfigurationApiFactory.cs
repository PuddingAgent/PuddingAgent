using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingAgentConfiguration;
using PuddingAgentConfiguration.Contracts;
using PuddingCode.Security;

namespace PuddingPlatform.Services;

/// <summary>
/// Trusted management entry point. This grants only one addressed Agent and only metadata configuration.
/// Plugin/external-token delegation requires its own grant adapter; this factory does not grant those callers access.
/// </summary>
public sealed class AgentConfigurationApiFactory(IAgentConfigurationStore store)
{
    public IAgentConfigurationApi ForManagementUser(ClaimsPrincipal principal, AgentRef scope)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new ConfigurationApiException("permission.denied", "A management login is required.");
        var caller = new ConfigurationCaller(subject);
        return new AgentConfigurationService(store, new ManagementGrant(caller, scope)).ForCaller(caller);
    }

    private sealed class ManagementGrant(ConfigurationCaller caller, AgentRef scope) : IAgentConfigurationAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(ConfigurationCaller actual, AgentRef target,
            ConfigurationAccess access, CancellationToken ct) => ValueTask.FromResult(actual == caller && target == scope);
    }
}
