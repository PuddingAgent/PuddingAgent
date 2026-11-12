using System.Collections.Immutable;
using PuddingContext.Contracts;

namespace PuddingContext;

/// <summary>
/// PA-05D ordered assembly and bounded trimming: canonical history keeps its exact order and the current user
/// fence is never dropped; contributions are ordered deterministically (priority desc, id ordinal) and trimmed
/// only from the lowest priority. A canonical history that alone exceeds the budget is a typed failure, never a
/// silent truncation; a missing/duplicated fence or tokenizer failure is likewise a typed failure.
/// </summary>
public sealed class ContextAssembler
{
    private readonly IContextTokenizer _tokenizer;

    public ContextAssembler(IContextTokenizer tokenizer)
        => _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));

    public async ValueTask<ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ContextContributionValidator.ValidateRequest(request);
        var canonicalTokens = 0;
        foreach (var message in request.CanonicalHistory)
            canonicalTokens += await CountAsync(message.Content, ct).ConfigureAwait(false);
        if (canonicalTokens > request.MaxTokens)
            throw Error(ContextErrorCodes.BudgetExceeded,
                "The canonical history alone exceeds the context budget; it is never silently truncated.");

        var messages = new List<ContextMessage>(request.CanonicalHistory);
        var used = new List<string>();
        var dropped = new List<string>();
        var total = canonicalTokens;
        var order = request.CanonicalHistory[^1].Order;
        foreach (var contribution in request.Contributions
                     .OrderByDescending(c => c.Priority)
                     .ThenBy(c => c.ContributionId, StringComparer.Ordinal))
        {
            ContextContributionValidator.VerifyHash(contribution);
            var tokens = await CountAsync(contribution.Content, ct).ConfigureAwait(false);
            if (tokens > contribution.MaxTokens)
                throw Error(ContextErrorCodes.ContributionTooLarge,
                    "The contribution exceeds its own token budget.");
            if (total + tokens > request.MaxTokens)
            {
                dropped.Add(contribution.ContributionId);
                continue;
            }
            order++;
            messages.Add(new ContextMessage($"contribution:{contribution.ContributionId}", contribution.Role,
                contribution.Content, contribution.Source, order, contribution.ContentHash));
            used.Add(contribution.ContributionId);
            total += tokens;
        }
        return new(messages.ToImmutableArray(), total, dropped.ToImmutableArray(), used.ToImmutableArray(),
            Trimmed: dropped.Count > 0);
    }

    private async ValueTask<int> CountAsync(string content, CancellationToken ct)
    {
        try
        {
            var count = await _tokenizer.CountAsync(content, ct).ConfigureAwait(false);
            if (count < 0) throw Error(ContextErrorCodes.TokenizerUnavailable, "The tokenizer returned an invalid count.");
            return count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ContextException) { throw; }
        catch (Exception) { throw Error(ContextErrorCodes.TokenizerUnavailable, "The tokenizer is unavailable."); }
    }

    private static ContextException Error(string code, string message) => new(code, message);
}
