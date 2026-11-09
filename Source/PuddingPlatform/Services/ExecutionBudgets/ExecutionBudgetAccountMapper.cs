using System.Collections.Immutable;
using PuddingExecutionResources.Contracts;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// Host-bound canonical account request. <paramref name="ParentAccountId"/> must reference an existing trusted
/// account; callers never pass a bare business id for the mapper to guess a type.
/// </summary>
public sealed record BudgetAccountDescriptor(
    BudgetAccountKind RootKind, string RootId, string WorkspaceId,
    string AgentId, string SessionId, BudgetLimits Limits, string CostUnit,
    BudgetEnforcementMode Mode, BudgetAccountId? ParentAccountId = null)
{
    public bool IsValid =>
        Enum.IsDefined(RootKind) && Enum.IsDefined(Mode)
        && Bounded(RootId)
        && Prefix(RootKind) is { } prefix && prefix.Length + RootId.Length <= 128
        && !string.IsNullOrWhiteSpace(WorkspaceId) && WorkspaceId.Length <= 128
        && !string.IsNullOrWhiteSpace(AgentId) && AgentId.Length <= 128
        && !string.IsNullOrWhiteSpace(SessionId) && SessionId.Length <= 128
        && Limits is not null
        && !string.IsNullOrWhiteSpace(CostUnit) && CostUnit.Length <= 32
        && (ParentAccountId is null || BoundedParent(ParentAccountId))
        // GoalRun and Task accounts can only be roots; only Work may be created under an existing account.
        && (RootKind == BudgetAccountKind.Work || ParentAccountId is null);

    /// <summary>Stable account-id prefix; the existing id format is preserved by this task.</summary>
    public static string? Prefix(BudgetAccountKind kind) => kind switch
    {
        BudgetAccountKind.GoalRun => "goal:",
        BudgetAccountKind.Work => "work:",
        BudgetAccountKind.Task => "task:",
        _ => null,
    };

    private static bool Bounded(string? value) => value is { Length: > 0 and <= 128 }
        && value == value.Trim() && value.IndexOf(',') < 0 && !value.Any(char.IsControl);

    private static bool BoundedParent(BudgetAccountId parent) => parent.IsValid && Bounded(parent.Value);
}

public sealed record BudgetAccountScope(BudgetAccountId AccountId, ImmutableArray<BudgetAccountId> Ancestors,
    BudgetIdentity Identity, BudgetEnforcementMode Mode, string CostUnit);

/// <summary>
/// PA-01 P2 canonical account mapping: one deterministic root per GoalRun/Work/Task, with an explicit existing parent
/// account reference for descendants. Every resolution goes through the atomic store binding entry point, so limits,
/// mode, parent and identity are frozen at creation and a later conflicting descriptor is rejected instead of reused.
/// </summary>
public sealed class ExecutionBudgetAccountMapper(ExecutionBudgetStore store)
{
    public async Task<BudgetAccountScope> ResolveAsync(BudgetAccountDescriptor descriptor,
        CancellationToken ct = default)
    {
        if (descriptor is null || !descriptor.IsValid)
            throw new ExecutionResourceException(BudgetErrorCodes.AccountInvalid, "A valid budget account descriptor is required.");

        var identity = new BudgetIdentity(descriptor.RootKind, descriptor.RootId, descriptor.WorkspaceId,
            descriptor.AgentId, descriptor.SessionId);
        var expected = new BudgetAccountDefinition(AccountId(descriptor), descriptor.ParentAccountId, identity,
            descriptor.Limits, descriptor.CostUnit, descriptor.Mode);
        // The store performs the atomic read-validate-create/reuse; the mapper neither guesses a parent type nor
        // creates a parent account, and it never swallows a typed budget failure.
        var binding = await store.EnsureAccountBindingAsync(expected, ct).ConfigureAwait(false);
        return new(binding.Definition.AccountId, binding.Ancestors, binding.Definition.Identity,
            binding.Definition.Mode, binding.Definition.CostUnit);
    }

    private static BudgetAccountId AccountId(BudgetAccountDescriptor d) =>
        new((BudgetAccountDescriptor.Prefix(d.RootKind)
             ?? throw new ExecutionResourceException(BudgetErrorCodes.AccountInvalid, "The account kind is not supported."))
            + d.RootId);
}
