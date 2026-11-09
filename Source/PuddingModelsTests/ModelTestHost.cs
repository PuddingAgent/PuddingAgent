using System.Collections.Immutable;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingModelsTests;

/// <summary>Independent fake for the PA-06 configuration authority: layer seeds, CAS revision and typed patch application.</summary>
public sealed class FakeModelPolicyStore : IAgentModelPolicyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    public int Writes { get; private set; }
    public int Conflicts { get; private set; }

    public void Seed(string workspaceId, string agentId, string purpose, PolicySourceKind source, string modelId,
        long revision, bool inheritPrimary = false)
    {
        lock (_gate)
        {
            var state = StateOf(workspaceId, agentId);
            var layer = new PurposeModelLayer(modelId, inheritPrimary, source, revision);
            switch (source)
            {
                case PolicySourceKind.Workspace: state.Workspace[purpose] = layer; break;
                case PolicySourceKind.System: state.System[purpose] = layer; break;
                default: state.Instance[purpose] = layer; break;
            }
        }
    }

    public ValueTask<AgentConfigurationSnapshot> ReadAsync(AgentProfileScope scope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = StateOf(scope.WorkspaceId, scope.AgentId);
            var purposes = state.Instance.Keys.Concat(state.Workspace.Keys).Concat(state.System.Keys)
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)
                .Select(purpose => new PurposeModelSetting(purpose, state.Instance.GetValueOrDefault(purpose),
                    state.Workspace.GetValueOrDefault(purpose), state.System.GetValueOrDefault(purpose)))
                .ToImmutableArray();
            return ValueTask.FromResult(new AgentConfigurationSnapshot(state.Revision, state.Generation, purposes));
        }
    }

    public ValueTask<ModelPolicyWriteResult> WriteAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = StateOf(scope.WorkspaceId, scope.AgentId);
            if (command.ExpectedRevision != state.Revision)
            {
                Conflicts++;
                throw new ModelException(ModelErrorCodes.ConfigRevisionConflict,
                    "The instance revision changed; the losing writer must not overwrite the winner.");
            }
            foreach (var patch in command.Patches)
            {
                if (patch.Clear) state.Instance.Remove(patch.Purpose);
                else state.Instance[patch.Purpose] = new PurposeModelLayer(patch.ModelId, patch.InheritPrimary,
                    PolicySourceKind.Instance, state.Revision + 1);
            }
            state.Revision++;
            state.Generation++;
            Writes++;
            return ValueTask.FromResult(new ModelPolicyWriteResult(state.Revision, state.AppliedRevision,
                AppliesFromNextRun: true, [], state.Generation));
        }
    }

    public AgentConfigurationSnapshot SnapshotOf(string workspaceId, string agentId)
        => ReadAsync(new(workspaceId, agentId, "actor-1")).GetAwaiter().GetResult();

    private State StateOf(string workspaceId, string agentId)
    {
        var key = workspaceId + "|" + agentId;
        if (!_states.TryGetValue(key, out var state)) _states[key] = state = new State();
        return state;
    }

    private sealed class State
    {
        public long Revision;
        public long AppliedRevision;
        public long Generation;
        public readonly Dictionary<string, PurposeModelLayer?> Instance = new(StringComparer.Ordinal);
        public readonly Dictionary<string, PurposeModelLayer> Workspace = new(StringComparer.Ordinal);
        public readonly Dictionary<string, PurposeModelLayer> System = new(StringComparer.Ordinal);
    }
}

public sealed class FakeQuoteSource : IModelQuoteSource
{
    public ModelQuoteSnapshot? Quote { get; set; }
    public List<ModelQuoteRequest> Requests { get; } = [];

    public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult(Quote);
    }
}

public sealed class FakeCatalogSource : IModelCatalogSource
{
    private readonly Dictionary<string, ModelCapabilityRecord> _models = new(StringComparer.Ordinal);

    public void Add(ModelCapabilityRecord record) => _models[record.ModelId] = record;

    public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
        => ValueTask.FromResult(_models.GetValueOrDefault(modelId));

    public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
        int maxCount, CancellationToken ct = default)
        => ValueTask.FromResult(_models.Values.OrderBy(m => m.ModelId, StringComparer.Ordinal).Take(maxCount)
            .ToImmutableArray());
}

internal static class ModelFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static AgentProfileScope Scope(string workspace = "ws-1", string agent = "agent-1")
        => new(workspace, agent, "actor-1");

    public static UpdateModelPolicyCommand Update(long expectedRevision, params ModelPolicyPatch[] patches)
        => new("op-1", expectedRevision, [.. patches], "next-run");

    public static ModelPolicyPatch Set(string purpose, string modelId) => new(purpose, modelId, Clear: false);
    public static ModelPolicyPatch Clear(string purpose) => new(purpose, null, Clear: true);
    public static ModelPolicyPatch Inherit(string purpose) => new(purpose, null, Clear: false, InheritPrimary: true);

    public static ModelQuoteSnapshot Quote(string currency = "usd", decimal? input = 2m, decimal? cacheHit = 0.5m,
        decimal? output = 4m, bool explicitFree = false, string priceSource = "declared", decimal maxChargeable = 100m,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
        => new("quote-1", "model-1", "revision-1", currency, "per-million", input, cacheHit, output, 1_000_000,
            from ?? Now.AddHours(-1), to ?? Now.AddHours(1), "UTC", priceSource, explicitFree, maxChargeable,
            "pricing-v1", DateTimeOffset.MinValue);

    public static ModelCapabilityRecord Capability(string modelId, string revision = "revision-1",
        decimal? input = 2m, decimal? output = 4m, bool explicitFree = false)
        => new(modelId, revision, 128_000, 8_192, ["text"], SupportsTools: true, SupportsStructuredOutput: true,
            "available", input, 0.5m, output, "usd", "declared", explicitFree, Now.AddHours(-1), Now.AddHours(1),
            "UTC", 100m, "pricing-v1");

    public static ModelUsage Usage(long input, long output, long cacheHit = 0) => new(input, output, cacheHit);
}
