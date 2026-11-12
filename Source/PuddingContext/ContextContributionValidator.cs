using System.Security.Cryptography;
using System.Text;
using PuddingContext.Contracts;

namespace PuddingContext;

/// <summary>
/// PA-05D contribution fence: the canonical history must end with the current user turn whose content hash is the
/// caller-provided fence hash, canonical order must be strictly ascending, ids are unique, and external/tool/
/// memory/caller data can only appear as a Tool-role contribution — never impersonating a user/system instruction.
/// </summary>
public sealed class ContextContributionValidator
{
    public static void ValidateRequest(ContextAssemblyRequest request)
    {
        if (request is null || !request.IsValid)
            throw Error(ContextErrorCodes.RequestInvalid, "A bounded context assembly request is required.");
        if (request.CanonicalHistory.IsEmpty)
            throw Error(ContextErrorCodes.FenceMissing, "Canonical history is required and must end with the current user turn.");
        var last = request.CanonicalHistory[^1];
        if (last.Role != ContextRole.User || string.IsNullOrWhiteSpace(last.ContentHash))
            throw Error(ContextErrorCodes.FenceMissing, "The canonical history must end with the current user fence.");
        if (!string.Equals(last.ContentHash, request.CurrentUserFenceHash, StringComparison.Ordinal))
            throw Error(ContextErrorCodes.FenceMismatch, "The current user fence hash does not match the canonical turn.");
        var order = int.MinValue;
        foreach (var message in request.CanonicalHistory)
        {
            if (message is null || !ContextAssemblyRequest.Valid(message.MessageId) || string.IsNullOrEmpty(message.Content)
                || message.Order <= order)
                throw Error(ContextErrorCodes.RequestInvalid, "Canonical messages require unique ids and strictly ascending order.");
            order = message.Order;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in request.Contributions)
        {
            ValidateContribution(contribution, request.MaxTokens);
            if (!ids.Add(contribution.ContributionId))
                throw Error(ContextErrorCodes.ContributionInvalid, "Contribution ids must be unique.");
        }
    }

    public static void ValidateContribution(ContextContribution? contribution, int budgetTokens)
    {
        if (contribution is null || !ContextAssemblyRequest.Valid(contribution.ContributionId)
            || string.IsNullOrWhiteSpace(contribution.Content) || contribution.Content.Length > 200_000
            || contribution.Priority is < -1000 or > 1000 || contribution.MaxTokens is < 1 || contribution.LifetimeTurns is < 0 or > 1000
            || contribution.MaxTokens > budgetTokens || contribution.Source == ContextSourceKind.Canonical)
            throw Error(ContextErrorCodes.ContributionInvalid, "The contribution fields are invalid.");
        var external = contribution.ExternalData || contribution.Source is ContextSourceKind.Memory
            or ContextSourceKind.Tool or ContextSourceKind.CallerSupplied;
        if (external && contribution.Role != ContextRole.Tool)
            throw Error(ContextErrorCodes.ContributionImpersonation,
                "External, memory, tool or caller-supplied data cannot impersonate a user or system instruction.");
        if (contribution.Source == ContextSourceKind.System && contribution.Role != ContextRole.System)
            throw Error(ContextErrorCodes.ContributionInvalid, "A system contribution must use the system role.");
        if (contribution.Source == ContextSourceKind.CallerSupplied && contribution.Role != ContextRole.Tool)
            throw Error(ContextErrorCodes.ContributionImpersonation, "Caller-supplied content must stay tool data.");
    }

    public static void VerifyHash(ContextContribution contribution)
    {
        if (string.IsNullOrWhiteSpace(contribution.ContentHash)) return;
        var actual = Hash(contribution.Content);
        if (!string.Equals(actual, contribution.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw Error(ContextErrorCodes.HashMismatch, "The contribution content hash does not match its content.");
    }

    public static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static ContextException Error(string code, string message) => new(code, message);
}
