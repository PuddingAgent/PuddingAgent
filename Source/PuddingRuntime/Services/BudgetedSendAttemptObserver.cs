using System.Collections.Immutable;
using PuddingCode.Observability;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;

namespace PuddingRuntime.Services;

/// <summary>
/// CORE-I1-06: turns every real provider send of a managed invocation into its own ledger attempt. The Work-level
/// identity/limits come from the frozen <see cref="LlmAttemptBudget"/>; each send gets a fresh attempt id, the price is
/// quoted by the platform port at dispatch time, and the attempt is settled with the actual usage, marked unknown when the
/// effect cannot be proven, or kept unusable when the send was never admitted.
/// </summary>
public sealed class BudgetedSendAttemptObserver(
    IModelAttemptBudgetPort port,
    LlmAttemptBudget budget,
    Func<DateTimeOffset>? clock = null) : ILlmSendAttemptObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelAttemptBudgetGrant> _grants = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async ValueTask<LlmSendPermit?> BeforeSendAsync(LlmSendAttemptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!budget.IsValid)
        {
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid,
                "The host-bound budget identity is invalid; refusing to send.");
        }

        var attemptId = Guid.NewGuid().ToString("N");
        var maxOutput = budget.MaxOutputTokens;
        if (request.MaxOutputTokens > 0 && request.MaxOutputTokens < maxOutput)
            maxOutput = request.MaxOutputTokens;
        var claim = new ModelAttemptBudgetClaim(
            budget.AccountId,
            [.. budget.Ancestors],
            new BudgetIdentity(
                Enum.TryParse<BudgetAccountKind>(budget.RootKind, ignoreCase: false, out var kind)
                    ? kind
                    : throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid,
                        "The budget root kind is not supported."),
                budget.RootId, budget.WorkspaceId, budget.AgentId, budget.SessionId),
            attemptId,
            request.ModelId,
            string.IsNullOrWhiteSpace(request.ModelRevision) ? budget.ModelRevision : request.ModelRevision,
            string.IsNullOrWhiteSpace(request.PriceVersion) ? budget.PriceVersion : request.PriceVersion,
            request.MaxInputTokens > 0 ? request.MaxInputTokens : budget.MaxInputTokens,
            maxOutput,
            budget.CostUnit,
            request.DispatchAt == default ? _clock() : request.DispatchAt);

        var grant = await port.BeginAsync(claim, ct).ConfigureAwait(false);
        if (grant is null)
            return LlmSendPermit.Ungated(attemptId);

        lock (_gate)
        {
            _grants[attemptId] = grant;
        }
        return new LlmSendPermit(attemptId, maxOutput, Gated: true);
    }

    public async ValueTask CompleteAsync(LlmSendPermit? permit, LlmSendUsage? usage, string? providerReceipt,
        string? failureReason, CancellationToken ct = default)
    {
        if (permit is null || !permit.Gated)
            return;
        ModelAttemptBudgetGrant? grant;
        lock (_gate)
        {
            if (!_grants.Remove(permit.AttemptId, out grant))
                return; // already completed; a second completion must not touch the ledger
        }

        if (failureReason is not null)
        {
            await port.MarkUnknownAsync(grant, failureReason, ct).ConfigureAwait(false);
            return;
        }
        if (usage?.InputTokens is not { } input || usage.OutputTokens is not { } output)
        {
            await port.MarkUnknownAsync(grant, "llm.usage_unknown", ct).ConfigureAwait(false);
            return;
        }

        var modelUsage = new ModelUsage(input, output, Math.Clamp(usage.CacheHitTokens ?? 0, 0, input));
        await port.SettleAsync(grant, modelUsage,
            string.IsNullOrWhiteSpace(providerReceipt) ? $"attempt:{permit.AttemptId}" : providerReceipt, ct)
            .ConfigureAwait(false);
    }
}
