using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingAgentWork;
using PuddingAgentWork.Contracts;
using PuddingCode.Security;

namespace PuddingPlatform.Services.AgentWork;

/// <summary>Management-only bootstrap grant. Does not confer management privileges on plugins or external access tokens.</summary>
public sealed class AgentWorkApiFactory(IAgentWorkBackend backend)
{
    public IAgentWorkApi ForManagementUser(ClaimsPrincipal principal, AgentRef scope)
    {
        var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(actor) || actor.Length > 64
            || actor.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new AgentWorkException("permission.denied", "A management login is required.");
        var caller = new WorkCaller(actor);
        return new AgentWorkService(backend, new Grant(caller, scope)).ForCaller(caller);
    }
    private sealed class Grant(WorkCaller caller, AgentRef scope) : IAgentWorkAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(WorkCaller actual, AgentRef target, WorkAccess access, CancellationToken ct)
            => ValueTask.FromResult(actual == caller && target == scope);
    }
}
