using PuddingCode.Abstractions;
using PuddingPlatform.Services.Models;

namespace PuddingHost.Services;

/// <summary>
/// PA-06B adapter view over the existing LLM configuration authority (llm.providers.json). It exposes no credentials or
/// endpoints; the catalog source derives authorized capability/pricing fields from this stable view.
/// </summary>
public sealed class LlmConfigModelCatalogView(ILlmConfigService configs) : ILlmModelCatalogView
{
    public IReadOnlyList<LlmCatalogModel> List() => configs.GetAllModels()
        .Select(m => new LlmCatalogModel(m.ModelId, m.ProviderId, Math.Max(1, m.MaxContextTokens),
            Math.Max(1, m.MaxInputTokens ?? m.MaxContextTokens), Math.Max(1, m.MaxOutputTokens),
            m.InputPricePer1MTokens, m.OutputPricePer1MTokens, m.CacheHitPricePer1MTokens,
            m.PriceWindowProfileVersion, m.IsDeprecated, m.IsEmbedding))
        .ToList();
}
