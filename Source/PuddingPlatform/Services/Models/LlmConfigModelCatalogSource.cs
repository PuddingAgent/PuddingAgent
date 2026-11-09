using System.Collections.Immutable;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Models;

/// <summary>Host-provided LLM configuration view (provider/model/prices), kept separate from the catalog contract.</summary>
public sealed record LlmCatalogModel(string ModelId, string ProviderId, int MaxContextTokens, int MaxInputTokens,
    int MaxOutputTokens, decimal InputPricePerMillion, decimal OutputPricePerMillion,
    decimal CacheHitPricePerMillion, string? PriceWindowProfileVersion, bool IsDeprecated, bool IsEmbedding);

public interface ILlmModelCatalogView
{
    IReadOnlyList<LlmCatalogModel> List();
}

/// <summary>
/// PA-06B catalog adapter over the existing llm.providers.json authority: exposes only authorized identity/limits/pricing
/// fields. A zero/negative configured price is reported as "unknown" (null), never as an explicit free model.
/// </summary>
public sealed class LlmConfigModelCatalogSource(ILlmModelCatalogView view) : IModelCatalogSource
{
    public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
        => ValueTask.FromResult(Find(modelId));

    public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
        int maxCount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || maxCount is < 1 or > 100)
            throw new ModelException(ModelErrorCodes.RequestInvalid, "A bounded catalog query is required.");
        var after = string.IsNullOrWhiteSpace(cursor) ? null : cursor;
        var items = view.List()
            .Where(m => !m.IsEmbedding && m.ModelId.Length <= 128)
            .OrderBy(m => m.ModelId, StringComparer.Ordinal)
            .Where(m => after is null || string.CompareOrdinal(m.ModelId, after) > 0)
            .Take(maxCount)
            .Select(Map)
            .ToImmutableArray();
        return ValueTask.FromResult(items);
    }

    private ModelCapabilityRecord? Find(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 128)
            throw new ModelException(ModelErrorCodes.RequestInvalid, "A bounded model id is required.");
        var model = view.List().FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.Ordinal));
        return model is null ? null : Map(model);
    }

    private static ModelCapabilityRecord Map(LlmCatalogModel m)
    {
        static decimal? Price(decimal value) => value > 0m ? value : null;
        var revision = string.IsNullOrWhiteSpace(m.PriceWindowProfileVersion) ? "config" : m.PriceWindowProfileVersion;
        return new ModelCapabilityRecord(m.ModelId, revision, m.MaxContextTokens, m.MaxOutputTokens,
            ["text"], false, false, m.IsDeprecated ? "deprecated" : "available",
            InputPerMillion: Price(m.InputPricePerMillion),
            CacheHitPerMillion: Price(m.CacheHitPricePerMillion),
            OutputPerMillion: Price(m.OutputPricePerMillion),
            Currency: "usd", PriceSource: m.PriceWindowProfileVersion is null ? "configured" : "price-window",
            ExplicitFree: false, QuoteValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
            QuoteValidTo: DateTimeOffset.UtcNow.AddDays(30), TimeZoneId: "utc", MaxChargeable: 100m,
            PricingRuleVersion: revision);
    }
}
