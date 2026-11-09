using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Models;

/// <summary>
/// PA-06C quote adapter over the authorized catalog: a quote is produced only for the exact frozen model revision,
/// inside the declared price window, in the account currency, and only when prices are known — missing prices become
/// "no quote" instead of zero; only an explicit-free model may quote zero.
/// </summary>
public sealed class CatalogModelQuoteSource(IModelCatalogSource source, TimeProvider? clock = null) : IModelQuoteSource
{
    public async ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ModelId) || request.ModelId.Length > 128
            || string.IsNullOrWhiteSpace(request.Revision) || request.Revision.Length > 128
            || string.IsNullOrWhiteSpace(request.AccountCurrency) || request.AccountCurrency.Length > 16)
            throw new ModelException(ModelErrorCodes.RequestInvalid, "A bounded quote request is required.");
        var capability = await source.GetAsync(request.ModelId, ct).ConfigureAwait(false);
        if (capability is null) return null;
        if (!string.Equals(capability.Revision, request.Revision, StringComparison.Ordinal)) return null;
        if (!string.Equals(capability.Currency, request.AccountCurrency, StringComparison.Ordinal)) return null;
        if (!capability.ExplicitFree
            && (capability.InputPerMillion is null || capability.OutputPerMillion is null))
            return null;
        if (request.DispatchAt < capability.QuoteValidFrom || request.DispatchAt >= capability.QuoteValidTo) return null;
        var input = capability.ExplicitFree ? 0m : capability.InputPerMillion!.Value;
        var output = capability.ExplicitFree ? 0m : capability.OutputPerMillion!.Value;
        var cacheHit = capability.CacheHitPerMillion ?? input;
        return new ModelQuoteSnapshot(
            QuoteId: $"quote:{request.ModelId}:{capability.Revision}:{request.AccountCurrency}:{request.DispatchAt:O}:{capability.PricingRuleVersion}",
            request.ModelId, capability.Revision, capability.Currency, "per-1m-tokens", input, cacheHit, output,
            BaseUnit: 1_000_000, capability.QuoteValidFrom, capability.QuoteValidTo,
            capability.TimeZoneId, capability.PriceSource, capability.ExplicitFree, capability.MaxChargeable,
            capability.PricingRuleVersion, (clock ?? TimeProvider.System).GetUtcNow());
    }
}
