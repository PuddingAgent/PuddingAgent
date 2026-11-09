using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Models;

namespace PuddingPlatform.Services.Configuration;

/// <summary>Trusted entry for the public model catalog: only an authenticated management subject is bound as actor.</summary>
public sealed class ModelCatalogApiFactory(ModelCatalogAdapter catalog, IAgentModelPolicyStore policies,
    IModelCatalogSource source)
{
    public IModelCatalogApi ForManagementUser(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)
            || subject.StartsWith(ExternalAccessTokenDefaults.ActorIdPrefix, StringComparison.Ordinal)
            || principal.HasClaim(c => c.Type == ExternalAccessTokenClaimNames.TokenId))
            throw new ModelCatalogException(ModelErrorCodes.ScopeForbidden, "A management login is required.");
        return new PlatformModelCatalogApi(catalog, policies, source, subject);
    }
}
