using PuddingModels.Contracts;

namespace PuddingModels;

public enum QuoteDispatchDecision { Keep, Requote, Block }

public sealed record QuoteAdjustment(QuoteDispatchDecision Decision, ModelQuoteSnapshot? Quote, decimal AdditionalReserve,
    string? ReasonCode);

/// <summary>
/// PA-06C cross-window dispatch: a frozen quote is reused only while it is still usable at the actual dispatch time;
/// otherwise the caller must obtain a fresh quote and atomically adjust the PA-01 reservation. If the difference
/// cannot be covered by the remaining budget the dispatch is blocked — the old (cheaper) quotation is never reused.
/// </summary>
public static class QuoteWindowAdjuster
{
    public static QuoteAdjustment Adjust(ModelQuoteSnapshot? frozen, DateTimeOffset dispatchAt,
        ModelQuoteSnapshot? refreshed, long inputTokens, long outputTokens, long cacheHitTokens,
        decimal reservedMaxCost, decimal remainingBudget)
    {
        if (inputTokens < 0 || outputTokens < 0 || cacheHitTokens < 0 || cacheHitTokens > inputTokens
            || reservedMaxCost < 0)
            throw Error(ModelErrorCodes.RequestInvalid, "A bounded dispatch usage and reservation are required.");
        if (frozen is not null && frozen.IsUsableAt(dispatchAt))
            return new(QuoteDispatchDecision.Keep, frozen, 0m, null);
        if (refreshed is null)
            return new(QuoteDispatchDecision.Block, null, 0m, ModelErrorCodes.PriceUnknown);
        if (string.IsNullOrWhiteSpace(refreshed.Currency))
            return new(QuoteDispatchDecision.Block, null, 0m, ModelErrorCodes.PriceUnknown);
        if (!refreshed.ExplicitFree && (refreshed.InputPerMillion is null || refreshed.OutputPerMillion is null))
            return new(QuoteDispatchDecision.Block, null, 0m, ModelErrorCodes.PriceUnknown);
        var quoteCost = Cost(refreshed, inputTokens, outputTokens, cacheHitTokens);
        var additional = quoteCost - reservedMaxCost;
        if (additional > remainingBudget)
            return new(QuoteDispatchDecision.Block, null, 0m, ModelErrorCodes.QuoteCapExceeded);
        return new(QuoteDispatchDecision.Requote, refreshed, additional, null);
    }

    public static decimal Cost(ModelQuoteSnapshot quote, long inputTokens, long outputTokens, long cacheHitTokens)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (inputTokens < 0 || outputTokens < 0 || cacheHitTokens < 0 || cacheHitTokens > inputTokens
            || quote.BaseUnit <= 0)
            throw Error(ModelErrorCodes.RequestInvalid, "A bounded usage and positive base unit are required.");
        var normalInput = inputTokens - cacheHitTokens;
        return normalInput * (quote.InputPerMillion ?? 0m) / quote.BaseUnit
               + cacheHitTokens * (quote.CacheHitPerMillion ?? quote.InputPerMillion ?? 0m) / quote.BaseUnit
               + outputTokens * (quote.OutputPerMillion ?? 0m) / quote.BaseUnit;
    }

    private static ModelException Error(string code, string message) => new(code, message);
}
