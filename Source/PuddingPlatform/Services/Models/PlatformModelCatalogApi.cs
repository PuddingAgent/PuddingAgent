using Pudding.Api.Contracts;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Models;

/// <summary>
/// PA-06B public model catalog bound to one host-authorized actor: listing uses the authorized catalog projection;
/// resolving a purpose goes through the same policy authority as review routing (explicit InheritPrimary only).
/// </summary>
public sealed class PlatformModelCatalogApi(ModelCatalogAdapter catalog, IAgentModelPolicyStore policies,
    IModelCatalogSource source, string actorId) : IModelCatalogApi
{
    public Task<ModelCatalogPage> ListAsync(ModelQuery query, CancellationToken ct = default)
        => catalog.ListAsync(query, ct);

    public async Task<ModelSelection> ResolveAsync(ModelSelectionRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Agent is null
            || string.IsNullOrWhiteSpace(request.Agent.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.Agent.AgentId)
            || string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Length > 128)
            throw new ModelCatalogException(ModelErrorCodes.RequestInvalid, "A bounded selection request is required.");
        var scope = new AgentProfileScope(request.Agent.WorkspaceId, request.Agent.AgentId, actorId);
        EffectiveAgentModelPolicy effective;
        try
        {
            effective = await new ModelPolicyResolver(policies).ResolveAsync(scope, ct).ConfigureAwait(false);
        }
        catch (ModelException ex)
        {
            throw new ModelCatalogException(ex.Code, ex.Message);
        }
        var entry = effective.Purposes.FirstOrDefault(p =>
            string.Equals(p.Purpose, request.Purpose, StringComparison.Ordinal));
        if (entry is null || string.IsNullOrWhiteSpace(entry.ModelId))
            throw new ModelCatalogException(ModelErrorCodes.PolicyMissing,
                "The requested purpose has no configured model (only explicit InheritPrimary may borrow Primary).");
        var capability = await source.GetAsync(entry.ModelId!, ct).ConfigureAwait(false);
        if (capability is null || string.IsNullOrWhiteSpace(capability.Revision))
            throw new ModelCatalogException(ModelErrorCodes.CapabilityMissing,
                "The selected model has no authorized catalog revision.");
        var inherited = string.Equals(entry.Source, "inherited-primary", StringComparison.Ordinal);
        return new(entry.ModelId!, capability.Revision, inherited ? "inherited-primary" : "purpose", inherited);
    }
}
