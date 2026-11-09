using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>No host creation, credentials or state. Supply an already-authorized API from the host or plugin context.</summary>
public sealed class OperatorsClient(IOperatorApi api)
{
    public Task<OperatorCatalog> ListAsync(CancellationToken ct = default) => api.ListAsync(ct);
    public SkillValueScoring SkillValue { get; } = new(api);
}
public sealed class SkillValueScoring(IOperatorApi api)
{
    public Task<OperatorScoreReceipt> ScoreAsync(SkillValueInput input, OperatorCallOptions options, CancellationToken ct = default)
        => api.ScoreAsync(new(SkillValueApi.SceneKey, SkillValueApi.InputSchema, SkillValueApi.InputVersion,
            JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)), options), ct);
}
