using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Configuration;

/// <summary>
/// PA-06 public policy API bound to one host-authorized actor. Effective policy comes from the existing configuration
/// authority through ModelPolicyResolver; updates CAS on the manifest revision. It never supplies its own actor.
/// </summary>
public sealed class PlatformAgentModelPolicyApi(IAgentModelPolicyStore store, string actorId) : IAgentModelPolicyApi
{
    public async Task<EffectiveAgentModelPolicy?> GetEffectiveAsync(AgentRef agent, CancellationToken ct = default)
    {
        if (agent is null || string.IsNullOrWhiteSpace(agent.WorkspaceId) || string.IsNullOrWhiteSpace(agent.AgentId))
            throw new AgentModelPolicyException(ModelErrorCodes.RequestInvalid, "A bounded agent reference is required.");
        try
        {
            var resolved = await new ModelPolicyResolver(store).ResolveAsync(Scope(agent), ct).ConfigureAwait(false);
            return new EffectiveAgentModelPolicy(resolved.WorkspaceId, resolved.AgentId, resolved.EffectiveRevision,
                resolved.AppliedRevision, resolved.PolicyGeneration, resolved.ResolvedAt,
                resolved.Purposes.Select(p => new EffectiveModelEntry(p.Purpose, p.ModelId, p.ModelRevision, p.Source,
                    p.SourceRevision, p.MissingReason, p.InheritPrimary)).ToImmutableArray(),
                resolved.PendingReasons);
        }
        catch (ModelException ex)
        {
            throw new AgentModelPolicyException(ex.Code, ex.Message);
        }
    }

    public async Task<AgentModelPolicyReceipt> UpdateAsync(UpdateAgentModelPolicy request, CancellationToken ct = default)
    {
        if (request is null || request.Agent is null || string.IsNullOrWhiteSpace(request.OperationId)
            || request.Patches.IsDefaultOrEmpty)
            throw new AgentModelPolicyException(ModelErrorCodes.RequestInvalid, "A bounded policy update is required.");
        var patches = request.Patches.Select(p => new ModelPolicyPatch(p.Purpose, p.ModelId, p.Clear, p.InheritPrimary))
            .ToImmutableArray();
        try
        {
            var result = await store.WriteAsync(Scope(request.Agent),
                new UpdateModelPolicyCommand(request.OperationId, request.ExpectedRevision, patches,
                    request.ApplyPolicy), ct).ConfigureAwait(false);
            return new AgentModelPolicyReceipt(request.OperationId, result.PersistedRevision, result.AppliedRevision,
                result.AppliesFromNextRun, result.PendingReasons, result.PolicyGeneration);
        }
        catch (ModelException ex)
        {
            throw new AgentModelPolicyException(ex.Code, ex.Message);
        }
    }

    private AgentProfileScope Scope(AgentRef agent) => new(agent.WorkspaceId, agent.AgentId, actorId);
}
