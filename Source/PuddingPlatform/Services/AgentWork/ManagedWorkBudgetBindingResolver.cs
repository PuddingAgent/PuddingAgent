using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatform.Services.AgentWork;

/// <summary>
/// Resolves a canonical single-turn Work command into a frozen, host-bound budget binding. Trust chain:
/// command.ClientRequestId (correlation) + acceptance batch + the markers written by the frozen canonical Work
/// backend + <see cref="IAgentWorkBackend.ReadAsync(WorkKey)"/> verification. It never re-derives the Work hash and
/// never creates a second work state machine. Anything that looks managed but fails a check is an explicit error,
/// never an unmanaged fallback.
/// </summary>
public sealed class ManagedWorkBudgetBindingResolver(
    IExecutionCommandReader commandReader,
    IAgentWorkBackend workBackend,
    ExecutionBudgetAccountMapper accountMapper,
    ExecutionBudgetStore budgetStore,
    IModelCatalogSource modelCatalog,
    IManagedWorkBudgetPolicyProvider policyProvider,
    PlatformDbContext db) : IManagedWorkBudgetBindingResolver
{
    private const string OwnerMarker = "agent_work_actor";
    private const string OperationMarker = "agent_work_operation";
    private const string RequestHashMarker = "agent_work_request_hash";
    private const string ManagedWorkPrefix = "work-";
    /// <summary>Matches the public Work contract: <c>AgentWorkService</c> accepts operation ids up to 128 chars.</summary>
    private const int MaxMarkerLength = 128;

    public async Task<ManagedWorkBudgetBindingResult> ResolveAsync(
        ExecutionLease lease, ExecutionCommandRecord command, LlmInvocationProfile profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(profile);

        var clientRequestId = command.ClientRequestId;
        if (string.IsNullOrWhiteSpace(clientRequestId)
            || !clientRequestId.StartsWith(ManagedWorkPrefix, StringComparison.Ordinal))
            return ManagedWorkBudgetBindingResult.NotManaged;

        var policy = policyProvider.GetPolicy();
        if (policy is null)
            return ManagedWorkBudgetBindingResult.NotManaged;

        // A managed-prefixed command with an invalid policy must fail instead of falling back to an ungated path.
        var limits = ManagedWorkBudgetPolicyValidator.ValidateLimits(policy);
        var mode = ManagedWorkBudgetPolicyValidator.ParseMode(policy.Mode);

        if (command.CommandId != lease.CommandId
            || command.TurnId != lease.TurnId
            || command.ConversationId != lease.ConversationId
            || command.WorkspaceId != lease.WorkspaceId
            || string.IsNullOrWhiteSpace(command.UserId))
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.IdentityMismatch,
                "The command identity does not match the execution lease.");
        }

        var batch = await db.AcceptanceBatches.AsNoTracking().SingleOrDefaultAsync(
            item => item.WorkspaceId == lease.WorkspaceId && item.ClientRequestId == clientRequestId, ct)
            .ConfigureAwait(false);
        if (batch is null
            || batch.Status != "accepted"
            || batch.TurnCount != 1
            || !string.Equals(batch.UserId, command.UserId, StringComparison.Ordinal)
            || !string.Equals(batch.MessageId, command.UserMessageId, StringComparison.Ordinal)
            || !string.Equals(batch.ConversationId, lease.ConversationId, StringComparison.Ordinal))
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.EvidenceMissing,
                "The canonical acceptance row for this Work is missing or inconsistent.");
        }

        var input = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
            item => item.MessageId == batch.MessageId
                && item.WorkspaceId == lease.WorkspaceId
                && item.SessionId == lease.ConversationId, ct).ConfigureAwait(false);
        var operation = input is null ? null : ReadOperation(input.MetadataJson, command.UserId);
        if (operation is null)
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.EvidenceMissing,
                "The canonical Work acceptance markers on the input message are missing.");
        }

        AgentWorkSnapshot? snapshot;
        try
        {
            snapshot = await workBackend.ReadAsync(new WorkKey(
                new WorkCaller(command.UserId),
                new AgentRef(lease.WorkspaceId, command.AgentInstanceId),
                operation, clientRequestId), ct).ConfigureAwait(false);
        }
        catch (AgentWorkException ex)
        {
            throw new ManagedWorkBudgetException(
                ex.Code == "receipt.invalid"
                    ? ManagedWorkBudgetErrorCodes.IdentityMismatch
                    : ManagedWorkBudgetErrorCodes.EvidenceMissing,
                $"The canonical Work receipt could not be verified: {ex.Message}");
        }

        if (snapshot is null)
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.EvidenceMissing,
                "The canonical Work receipt is missing for the accepted command.");
        }

        var receipt = snapshot.Receipt;
        if (!string.Equals(receipt.WorkId, clientRequestId, StringComparison.Ordinal)
            || !string.Equals(receipt.CommandId, lease.CommandId, StringComparison.Ordinal)
            || !string.Equals(receipt.TurnId, lease.TurnId, StringComparison.Ordinal)
            || !string.Equals(receipt.ConversationId, lease.ConversationId, StringComparison.Ordinal)
            || !string.Equals(receipt.Agent.WorkspaceId, lease.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(receipt.Agent.AgentId, command.AgentInstanceId, StringComparison.Ordinal))
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.IdentityMismatch,
                "The canonical Work receipt does not match the leased command/turn identity.");
        }

        // CORE-I1-05 W3: an existing Work keeps its frozen definition. The host policy only defines new Work; a
        // recovered/re-leased Work must reuse the persisted limits/mode/currency, otherwise a policy change would
        // either fail an in-flight Work (budget.conflict) or silently widen its budget. The definition-conflict check
        // in the mapper is retained for explicit conflicting resolutions (DS-I1-04B).
        var accountId = new BudgetAccountId("work:" + receipt.WorkId);
        var bindingLimits = limits;
        var bindingMode = mode;
        var bindingCostUnit = policy.CostUnit;
        var hasPersistedDefinition = false;
        try
        {
            var persistedLimits = await budgetStore.ReadAsync(accountId, ct).ConfigureAwait(false);
            bindingLimits = persistedLimits.Limits;
            bindingMode = await budgetStore.GetEnforcementModeAsync(accountId, ct).ConfigureAwait(false);
            bindingCostUnit = (await budgetStore.GetAccountIdentityAsync(accountId, ct).ConfigureAwait(false)).CostUnit;
            hasPersistedDefinition = true;
        }
        catch (ExecutionResourceException ex) when (ex.Code == BudgetErrorCodes.AccountMissing)
        {
            // New Work: the current host policy is the frozen definition.
        }

        BudgetAccountScope account;
        try
        {
            account = await accountMapper.ResolveAsync(new BudgetAccountDescriptor(
                BudgetAccountKind.Work, receipt.WorkId, lease.WorkspaceId, command.AgentInstanceId,
                lease.ConversationId, bindingLimits, bindingCostUnit, bindingMode, ParentAccountId: null), ct)
                .ConfigureAwait(false);
        }
        catch (ExecutionResourceException ex)
        {
            // Account cancellation/conflict/missing evidence stays a typed budget failure, never a protocol error.
            throw new ManagedWorkBudgetException(ex.Code, ex.Message);
        }

        var model = await modelCatalog.GetAsync(profile.ModelId, ct).ConfigureAwait(false);
        if (model is null
            || string.IsNullOrWhiteSpace(model.Revision)
            || string.IsNullOrWhiteSpace(model.PricingRuleVersion)
            || !string.Equals(model.Currency, bindingCostUnit, StringComparison.Ordinal)
            || model.MaxOutputTokens <= 0
            || model.MaxContextTokens <= 0)
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.ModelUnpriced,
                $"The reviewed model catalog has no trustworthy revision/pricing/currency for '{profile.ModelId}'.");
        }

        // A recovered Work keeps its frozen per-request input capacity; only new Work takes the current policy value.
        var maxInputTokens = hasPersistedDefinition
            ? bindingLimits.MaxInputTokensPerRequest ?? model.MaxContextTokens
            : policy.MaxInputTokensPerRequest ?? model.MaxContextTokens;
        if (maxInputTokens <= 0)
        {
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.ModelUnpriced,
                "The per-attempt input capacity must be positive.");
        }

        var binding = new RuntimeWorkBudgetBinding(
            account.AccountId.Value,
            [.. account.Ancestors.Select(ancestor => ancestor.Value)],
            account.Identity.RootKind.ToString(),
            account.Identity.RootId,
            account.Identity.WorkspaceId,
            account.Identity.AgentId,
            account.Identity.SessionId,
            bindingCostUnit,
            model.Revision,
            model.PricingRuleVersion,
            maxInputTokens,
            model.MaxOutputTokens);

        return new ManagedWorkBudgetBindingResult(
            new ManagedWorkBudgetBinding(binding, receipt.WorkId, account.AccountId, policy));
    }

    /// <summary>Reads only the three markers written by the frozen canonical backend; anything else is not trusted.</summary>
    private static string? ReadOperation(string? metadataJson, string actorId)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;
        Dictionary<string, string>? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (metadata is null
            || !metadata.TryGetValue(OwnerMarker, out var owner)
            || !string.Equals(owner, actorId, StringComparison.Ordinal)
            || !metadata.TryGetValue(RequestHashMarker, out var hash)
            || string.IsNullOrWhiteSpace(hash)
            || !metadata.TryGetValue(OperationMarker, out var operation)
            || string.IsNullOrWhiteSpace(operation)
            || operation.Length > MaxMarkerLength)
        {
            return null;
        }

        return operation;
    }
}

public interface IManagedWorkBudgetBindingResolver
{
    /// <summary>Resolves a leased command into either an explicit unmanaged result or a frozen managed binding.</summary>
    Task<ManagedWorkBudgetBindingResult> ResolveAsync(
        ExecutionLease lease, ExecutionCommandRecord command, LlmInvocationProfile profile,
        CancellationToken ct = default);
}
