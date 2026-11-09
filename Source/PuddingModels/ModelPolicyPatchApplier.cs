using System.Collections.Immutable;
using PuddingModels.Contracts;

namespace PuddingModels;

/// <summary>
/// PA-06 06A patch application: validates a typed policy patch and applies it to the instance layer with CAS on the
/// frozen snapshot revision. Patch forms are explicit — Set (ModelId), Clear (fall back to workspace/system) or
/// InheritPrimary; a null ModelId alone is not an inheritance request. The result never changes another layer.
/// </summary>
public static class ModelPolicyPatchApplier
{
    public static ModelPolicyWriteResult Apply(AgentConfigurationSnapshot snapshot, UpdateModelPolicyCommand command,
        out AgentConfigurationSnapshot updated)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (command is null) throw Error(ModelErrorCodes.RequestInvalid, "A policy update command is required.");
        if (!Valid(command.OperationId) || command.ExpectedRevision < 0 || !Valid(command.ApplyPolicy))
            throw Error(ModelErrorCodes.RequestInvalid, "A bounded operation id, revision and apply policy are required.");
        if (command.Patches.IsDefault || command.Patches.IsEmpty || command.Patches.Length > 32)
            throw Error(ModelErrorCodes.RequestInvalid, "One to 32 policy patches are required.");
        var purposes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in command.Patches)
        {
            if (patch is null || !Valid(patch.Purpose) || !purposes.Add(patch.Purpose))
                throw Error(ModelErrorCodes.FieldUnknown, "A patch purpose is missing, unbounded or repeated.");
            if (patch.Clear && (patch.ModelId is not null || patch.InheritPrimary))
                throw Error(ModelErrorCodes.RequestInvalid, "Clear cannot be combined with a model or inheritance.");
            if (!patch.Clear && !patch.InheritPrimary && string.IsNullOrWhiteSpace(patch.ModelId))
                throw Error(ModelErrorCodes.RequestInvalid, "A patch must set a model, inherit primary or clear.");
        }
        if (snapshot.InstanceRevision != command.ExpectedRevision)
            throw Error(ModelErrorCodes.ConfigRevisionConflict, "The agent configuration revision changed.");
        var layers = snapshot.Purposes.ToImmutableDictionary(p => p.Purpose, StringComparer.Ordinal);
        foreach (var patch in command.Patches)
        {
            var existing = layers.TryGetValue(patch.Purpose, out var setting) ? setting : new PurposeModelSetting(patch.Purpose, null, null, null);
            PurposeModelLayer? instance = patch.Clear
                ? null
                : new PurposeModelLayer(patch.InheritPrimary ? null : patch.ModelId, patch.InheritPrimary, PolicySourceKind.Instance,
                    command.ExpectedRevision + 1);
            layers = layers.SetItem(patch.Purpose, existing with { Instance = instance });
        }
        var revision = command.ExpectedRevision + 1;
        updated = new(revision, snapshot.PolicyGeneration + 1, [.. layers.Values.OrderBy(p => p.Purpose, StringComparer.Ordinal)]);
        return new(revision, revision, AppliesFromNextRun: true, ImmutableArray<string>.Empty, snapshot.PolicyGeneration + 1);
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static ModelException Error(string code, string message) => new(code, message);
}
