using System.Collections.Immutable;
using PuddingCode.Configuration;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Configuration;

/// <summary>
/// PA-06 06A manifest mapping for the purpose model policy: the manifest is the only persistence authority; this mapper
/// converts its instance layer to the frozen contracts and applies a CAS patch via the component applier. It never
/// creates a second configuration file and never changes workspace/system layers.
/// </summary>
public static class AgentModelPolicyMapper
{
    public static AgentConfigurationSnapshot ToSnapshot(AgentModelPolicy? policy, long policyGeneration = 0)
    {
        var revision = policy?.Revision ?? 0;
        var settings = (policy?.Purposes ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Purpose))
            .GroupBy(p => p.Purpose, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(p => new PurposeModelSetting(p.Purpose,
                new PurposeModelLayer(p.InheritPrimary ? null : p.ModelId, p.InheritPrimary, PolicySourceKind.Instance,
                    revision), null, null))
            .ToImmutableArray();
        return new(revision, policy?.PolicyGeneration ?? policyGeneration, settings);
    }

    public static AgentModelPolicy ToPolicy(AgentConfigurationSnapshot snapshot) => new(snapshot.InstanceRevision,
        snapshot.PolicyGeneration, snapshot.Purposes
            .Where(p => p.Instance is { } layer && (layer.ModelId is not null || layer.InheritPrimary))
            .Select(p => new AgentPurposeModelPolicy(p.Purpose,
                p.Instance!.InheritPrimary ? null : p.Instance.ModelId, p.Instance.InheritPrimary))
            .OrderBy(p => p.Purpose, StringComparer.Ordinal)
            .ToList());

    /// <summary>CAS patch: a stale ExpectedRevision is rejected before any manifest write is attempted.</summary>
    public static AgentModelPolicy Apply(AgentModelPolicy? policy, UpdateModelPolicyCommand command,
        out ModelPolicyWriteResult writeResult)
    {
        var snapshot = ToSnapshot(policy);
        writeResult = ModelPolicyPatchApplier.Apply(snapshot, command, out var updated);
        return ToPolicy(updated);
    }
}
