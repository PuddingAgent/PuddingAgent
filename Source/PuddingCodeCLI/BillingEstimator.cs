namespace PuddingCodeCLI;

internal static class BillingEstimator
{
    public static string FormatCost(
        ProviderBillingConfig billing,
        long inputTokens,
        long outputTokens,
        int llmRequests,
        int sessions)
    {
        billing ??= new ProviderBillingConfig();

        return billing.Mode switch
        {
            BillingMode.LocalFree => "FREE (local)",
            BillingMode.MonthlyFlat => billing.MonthlyUsd > 0
                ? $"${billing.MonthlyUsd:0.####}/mo"
                : "Monthly plan",
            BillingMode.PerRequest => FormatPerRequest(billing, llmRequests),
            BillingMode.PerSession => FormatPerSession(billing, sessions),
            _ => FormatPerToken(billing, inputTokens, outputTokens)
        };
    }

    private static string FormatPerToken(ProviderBillingConfig billing, long inputTokens, long outputTokens)
    {
        if (billing.InputUsdPerMillionTokens <= 0 && billing.OutputUsdPerMillionTokens <= 0)
            return "N/A";

        var inputCost = (decimal)inputTokens / 1_000_000m * billing.InputUsdPerMillionTokens;
        var outputCost = (decimal)outputTokens / 1_000_000m * billing.OutputUsdPerMillionTokens;
        var total = inputCost + outputCost;
        return $"~${total:0.0000}";
    }

    private static string FormatPerRequest(ProviderBillingConfig billing, int llmRequests)
    {
        if (billing.RequestUsd <= 0)
            return billing.IncludedRequestsPerMonth > 0
                ? $"{llmRequests}/{billing.IncludedRequestsPerMonth} req"
                : "N/A";

        var total = billing.RequestUsd * llmRequests;
        return $"~${total:0.0000}";
    }

    private static string FormatPerSession(ProviderBillingConfig billing, int sessions)
    {
        if (billing.SessionUsd <= 0)
            return billing.IncludedSessionsPerMonth > 0
                ? $"{sessions}/{billing.IncludedSessionsPerMonth} sess"
                : "N/A";

        var total = billing.SessionUsd * sessions;
        return $"~${total:0.0000}";
    }
}

