using System.Collections.Immutable;
using PuddingModels.Contracts;
using Pudding.Api.Contracts;

namespace PuddingModels;

/// <summary>
/// PA-06A effective configuration: layering is instance → workspace → system (the existing resolver order),
/// every field exposes its source and revision, missing purposes stay missing instead of borrowing a default,
/// and writes are CAS on the instance revision. Only the frozen typed fields can be written.
/// </summary>
public sealed class ModelPolicyResolver
{
    private static readonly ImmutableArray<string> KnownPurposes = ["Primary", "Classification", "Review"];
    private static readonly ImmutableArray<string> KnownApplyPolicies = ["next-run", "immediate-safe"];

    private readonly IAgentModelPolicyStore _store;
    private readonly TimeProvider _clock;

    public ModelPolicyResolver(IAgentModelPolicyStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<EffectiveAgentModelPolicy> ResolveAsync(AgentProfileScope scope, CancellationToken ct = default)
    {
        EnsureScope(scope);
        var snapshot = await _store.ReadAsync(scope, ct).ConfigureAwait(false);
        // Every known purpose is represented so an unset purpose reports model_policy_missing instead of disappearing.
        var settings = KnownPurposes.Concat(snapshot.Purposes.Select(p => p.Purpose))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)
            .Select(purpose => snapshot.Purposes.FirstOrDefault(p => string.Equals(p.Purpose, purpose, StringComparison.Ordinal))
                               ?? new PurposeModelSetting(purpose, null, null, null))
            .ToImmutableArray();
        var entries = settings.Select(Resolve).ToImmutableArray();
        var primary = entries.FirstOrDefault(e => string.Equals(e.Purpose, "Primary", StringComparison.Ordinal));
        // Explicit "inherit Primary" only: nothing is borrowed implicitly.
        entries = entries.Select(entry =>
            entry.InheritPrimary && entry.ModelId is null
                ? primary is { ModelId: not null }
                    ? entry with { ModelId = primary.ModelId, ModelRevision = primary.ModelRevision, Source = "inherited-primary", SourceRevision = primary.SourceRevision, MissingReason = null }
                    : entry with { MissingReason = ModelErrorCodes.PolicyMissing }
                : entry).ToImmutableArray();
        return new(scope.WorkspaceId, scope.AgentId, snapshot.InstanceRevision, snapshot.InstanceRevision,
            snapshot.PolicyGeneration, _clock.GetUtcNow(), entries, []);
    }

    public async ValueTask<ModelPolicyWriteResult> UpdateAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
        CancellationToken ct = default)
    {
        EnsureScope(scope);
        Validate(command);
        return await _store.WriteAsync(scope, command, ct).ConfigureAwait(false);
    }

    public static EffectiveModelEntry Resolve(PurposeModelSetting setting)
    {
        var layer = setting.Instance ?? setting.Workspace ?? setting.System;
        if (layer is null)
            return new(setting.Purpose, null, null, null, 0, ModelErrorCodes.PolicyMissing);
        return new(setting.Purpose, layer.ModelId, layer.SourceRevision.ToString(), SourceName(layer.Source),
            layer.SourceRevision, null, layer.InheritPrimary);
    }

    private static void EnsureScope(AgentProfileScope scope)
    {
        if (scope is null || !scope.IsValid) throw Error(ModelErrorCodes.ScopeForbidden, "A valid host-bound agent scope is required.");
    }

    private static void Validate(UpdateModelPolicyCommand command)
    {
        if (command is null || !Valid(command.OperationId) || command.ExpectedRevision < 0)
            throw Error(ModelErrorCodes.RequestInvalid, "A bounded operation id and expected revision are required.");
        if (command.Patches.IsDefaultOrEmpty) throw Error(ModelErrorCodes.RequestInvalid, "At least one typed patch is required.");
        if (!KnownApplyPolicies.Contains(command.ApplyPolicy, StringComparer.Ordinal))
            throw Error(ModelErrorCodes.FieldUnknown, "The apply policy is not supported.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in command.Patches)
        {
            if (!KnownPurposes.Contains(patch.Purpose, StringComparer.Ordinal))
                throw Error(ModelErrorCodes.FieldUnknown, "The model purpose is not writable.");
            if (!seen.Add(patch.Purpose)) throw Error(ModelErrorCodes.RequestInvalid, "A purpose can be patched only once.");
            if (patch.Clear && (patch.ModelId is not null || patch.InheritPrimary))
                throw Error(ModelErrorCodes.RequestInvalid, "Clear cannot carry a model id or an inheritance flag.");
            if (!patch.Clear && !patch.InheritPrimary && !Valid(patch.ModelId))
                throw Error(ModelErrorCodes.RequestInvalid, "A model id is required unless the patch clears or inherits.");
        }
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static string SourceName(PolicySourceKind source) => source switch
    {
        PolicySourceKind.Instance => "instance",
        PolicySourceKind.Workspace => "workspace",
        _ => "system",
    };

    private static ModelException Error(string code, string message) => new(code, message);
}
