using PuddingModels.Contracts;

namespace PuddingModels;

/// <summary>
/// PA-06C quote freeze: an attempt is priced by the quote valid at the actual dispatch instant. Unknown price
/// is never zero; only ExplicitFree may be zero; currency must match the account; cache hits are charged at the
/// cache rate and the remaining input at the input rate so a token is never charged twice.
/// </summary>
public sealed class ModelQuoteService
{
    private readonly IModelQuoteSource _source;

    public ModelQuoteService(IModelQuoteSource source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    public async ValueTask<ModelQuoteSnapshot> FreezeAsync(string modelId, string revision,
        DateTimeOffset dispatchAt, string accountCurrency, CancellationToken ct = default)
    {
        if (!Valid(modelId) || !Valid(revision) || !Valid(accountCurrency))
            throw Error(ModelErrorCodes.RequestInvalid, "Bounded model identity and account currency are required.");
        var quote = await _source.QuoteAsync(new(modelId, revision, dispatchAt, accountCurrency), ct).ConfigureAwait(false)
            ?? throw Error(ModelErrorCodes.PriceUnknown, "No trustworthy price exists for this model revision.");
        if (!string.Equals(quote.Currency, accountCurrency, StringComparison.Ordinal))
            throw Error(ModelErrorCodes.CurrencyMismatch, "The quote currency differs from the account currency.");
        if (!quote.IsUsableAt(dispatchAt))
            throw Error(ModelErrorCodes.QuoteWindowMissed, "The quote window did not cover the actual dispatch instant.");
        if (quote.ExplicitFree) return quote with { FrozenAt = dispatchAt };
        if (string.IsNullOrWhiteSpace(quote.PriceSource) || quote.PriceSource is "unknown" or "none")
            throw Error(ModelErrorCodes.PriceUnknown, "The price source is not trustworthy; zero cannot be assumed.");
        if (quote.InputPerMillion is null || quote.OutputPerMillion is null || quote.CacheHitPerMillion is null)
            throw Error(ModelErrorCodes.PriceUnknown, "The price components are not declared; zero cannot be assumed.");
        if (quote.InputPerMillion < 0 || quote.OutputPerMillion < 0 || quote.CacheHitPerMillion < 0)
            throw Error(ModelErrorCodes.PriceUnknown, "A declared price cannot be negative.");
        if (quote.BaseUnit <= 0) throw Error(ModelErrorCodes.RequestInvalid, "The quote base unit is invalid.");
        return quote with { FrozenAt = dispatchAt };
    }

    /// <summary>Actual settlement for one attempt. Missing provider usage stays Unknown, never a fabricated charge.</summary>
    public static ModelSettlement Settle(ModelQuoteSnapshot quote, ModelUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.ExplicitFree) return new(0m, ReviewKnown: true);
        if (usage is null) return new(0m, ReviewKnown: false);
        if (quote.InputPerMillion is null || quote.OutputPerMillion is null || quote.CacheHitPerMillion is null)
            return new(0m, ReviewKnown: false);
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheHitTokens < 0
            || usage.CacheHitTokens > usage.InputTokens)
            throw Error(ModelErrorCodes.UsageUnknown, "The provider usage is invalid.");
        var cacheHit = usage.CacheHitTokens;
        var cacheMiss = usage.InputTokens - cacheHit;
        var cost = ((cacheMiss * quote.InputPerMillion.Value)
                    + (cacheHit * quote.CacheHitPerMillion!.Value)
                    + (usage.OutputTokens * quote.OutputPerMillion.Value)) / quote.BaseUnit;
        if (quote.MaxChargeable > 0 && cost > quote.MaxChargeable)
            throw Error(ModelErrorCodes.QuoteCapExceeded, "The actual charge exceeds the declared cap.");
        return new(cost, ReviewKnown: true);
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static ModelException Error(string code, string message) => new(code, message);
}

public sealed record ModelUsage(long InputTokens, long OutputTokens, long CacheHitTokens = 0);

public sealed record ModelSettlement(decimal Cost, bool ReviewKnown);
