using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PuddingCode.Security;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services;

/// <summary>
/// PA-06 host principal mapping: only an authenticated management login may read/write the purpose model policy. External
/// access tokens and unauthenticated requests are denied; plugin/background callers need their own verified adapter and
/// are not silently granted here. AgentId is never used as the caller identity.
/// </summary>
public sealed class ManagementModelPolicyAuthorization(IHttpContextAccessor accessor) : IAgentModelPolicyAuthorization
{
    public ValueTask<bool> AuthorizeReadAsync(AgentProfileScope scope, CancellationToken ct = default)
        => ValueTask.FromResult(Allowed(scope));

    public ValueTask<bool> AuthorizeWriteAsync(AgentProfileScope scope, long expectedRevision,
        CancellationToken ct = default) => ValueTask.FromResult(Allowed(scope));

    public bool Allowed(AgentProfileScope scope)
    {
        if (scope is null || !scope.IsValid) return false;
        var principal = accessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return false;
        if (principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId)) return false;
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal))
            return false;
        return string.Equals(subject, scope.ActorId, StringComparison.Ordinal);
    }
}
