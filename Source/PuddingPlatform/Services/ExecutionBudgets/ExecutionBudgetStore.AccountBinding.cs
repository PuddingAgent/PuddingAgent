using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>Platform-internal expected definition of one durable budget account binding (not a public contract).</summary>
public sealed record BudgetAccountDefinition(
    BudgetAccountId AccountId, BudgetAccountId? ParentAccountId,
    BudgetIdentity Identity, BudgetLimits Limits, string CostUnit,
    BudgetEnforcementMode Mode);

/// <summary>Persisted account definition plus its root→immediate-parent path (never containing the account itself).</summary>
public sealed record BudgetAccountBindingResult(
    BudgetAccountDefinition Definition,
    ImmutableArray<BudgetAccountId> Ancestors,
    bool Created);

/// <summary>
/// PA-01 P2 account binding entry point. It atomically reads the persisted account and its full parent chain, validates
/// the requested definition field by field, and creates or reuses the account inside one SQLite transaction. It never
/// creates a parent, never copies a caller-supplied ancestor list, never widens limits/mode and never upgrades a mode.
/// </summary>
public sealed partial class ExecutionBudgetStore
{
    private const int MaxAccountPathDepth = 64;
    private const int MaxBindingAttempts = 3;

    public async Task<BudgetAccountBindingResult> EnsureAccountBindingAsync(BudgetAccountDefinition expected,
        CancellationToken ct = default)
    {
        ValidateDefinition(expected);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= MaxBindingAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await EnsureBindingAsync(expected, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (attempt < MaxBindingAttempts && IsSqliteContention(exception))
                {
                    // A recognized unique-key race or BUSY/LOCKED: retry with a fresh context and transaction so the
                    // ordinary re-read + definition comparison decides whether this caller created or reused the row.
                }
            }
            throw new InvalidOperationException("The account binding retry loop ended without a result.");
        }
        finally { _gate.Release(); }
    }

    private async Task<BudgetAccountBindingResult> EnsureBindingAsync(BudgetAccountDefinition expected,
        CancellationToken ct)
    {
        // One transaction: read the persisted row and the whole required parent chain, validate, then insert the leaf.
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var accounts = await db.ExecutionBudgetAccounts
            .ToDictionaryAsync(account => account.AccountId, StringComparer.Ordinal, ct).ConfigureAwait(false);

        if (accounts.TryGetValue(expected.AccountId.Value, out var existing))
        {
            // A directly declared parent that does not exist is reported as missing before any field comparison.
            if (expected.ParentAccountId is { } declared && !accounts.ContainsKey(declared.Value))
                throw Error(BudgetErrorCodes.AccountMissing, "The declared parent budget account is missing.");
            // Read the persisted definition once: it owns the currency that already defines this account's chain.
            var persisted = Definition(existing);
            var ancestors = ExistingAncestors(accounts, existing, expected, persisted);
            EnsureSameDefinition(expected, persisted);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(persisted, ancestors, Created: false);
        }

        ImmutableArray<BudgetAccountId> parents;
        if (expected.ParentAccountId is { } parentId)
        {
            if (string.Equals(parentId.Value, expected.AccountId.Value, StringComparison.Ordinal))
                throw Error(BudgetErrorCodes.PathInvalid, "A budget account cannot be its own parent.");
            if (!accounts.TryGetValue(parentId.Value, out var parent))
                throw Error(BudgetErrorCodes.AccountMissing, "The declared parent budget account is missing.");
            parents = ParentAncestors(accounts, parent, expected);
        }
        else
        {
            // Non-default empty path: this account is the root of its own chain.
            parents = ImmutableArray<BudgetAccountId>.Empty;
        }

        var created = new ExecutionBudgetAccountEntity
        {
            AccountId = expected.AccountId.Value,
            ParentAccountId = expected.ParentAccountId?.Value,
            RootKind = (int)expected.Identity.RootKind,
            RootId = expected.Identity.RootId,
            WorkspaceId = expected.Identity.WorkspaceId,
            AgentId = expected.Identity.AgentId,
            SessionId = expected.Identity.SessionId,
            CostUnit = expected.CostUnit,
            MaxOutputTokens = expected.Limits.MaxOutputTokens,
            MaxCost = expected.Limits.MaxCost?.ToString("G29", CultureInfo.InvariantCulture),
            MaxCalls = expected.Limits.MaxCalls,
            MaxConcurrentAttempts = expected.Limits.MaxConcurrentAttempts,
            MaxInputTokensPerRequest = expected.Limits.MaxInputTokensPerRequest,
            EnforcementMode = (int)expected.Mode,
        };
        db.ExecutionBudgetAccounts.Add(created);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(Definition(created), parents, Created: true);
    }

    /// <summary>Ancestors of an already persisted account, read from its stored ParentAccountId chain (root→parent).</summary>
    private static ImmutableArray<BudgetAccountId> ExistingAncestors(
        Dictionary<string, ExecutionBudgetAccountEntity> accounts, ExecutionBudgetAccountEntity leaf,
        BudgetAccountDefinition expected, BudgetAccountDefinition persisted)
    {
        var ancestors = new List<ExecutionBudgetAccountEntity>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { leaf.AccountId };
        var current = leaf;
        while (current.ParentAccountId is { } parentId)
        {
            if (!visited.Add(parentId))
                throw Error(BudgetErrorCodes.PathInvalid, "The budget account parent chain is cyclic or self-referencing.");
            if (!accounts.TryGetValue(parentId, out var parent))
            {
                if (string.Equals(leaf.ParentAccountId, parentId, StringComparison.Ordinal)
                    && expected.ParentAccountId is { } declared
                    && string.Equals(declared.Value, parentId, StringComparison.Ordinal))
                    throw Error(BudgetErrorCodes.AccountMissing, "The declared parent budget account is missing.");
                throw Error(BudgetErrorCodes.PathInvalid, "The budget account ancestor chain is broken.");
            }
            if (current.RootKind is (int)BudgetAccountKind.GoalRun or (int)BudgetAccountKind.Task)
                throw Error(BudgetErrorCodes.PathInvalid, "GoalRun and Task accounts can only be roots.");
            ancestors.Add(parent);
            if (ancestors.Count + 1 > MaxAccountPathDepth)
                throw Error(BudgetErrorCodes.PathInvalid, "The budget account path exceeds the maximum depth.");
            current = parent;
        }
        ancestors.Reverse();
        // The persisted leaf currency defines the existing chain; only the request definition comparison below decides
        // that a requested currency change is a conflict (instead of misreporting it as an ancestor currency mismatch).
        ancestors.ForEach(ancestor => ValidateAncestor(expected, persisted.CostUnit, ancestor));
        if (leaf.Cancelled || ancestors.Exists(ancestor => ancestor.Cancelled))
            throw Error(BudgetErrorCodes.Cancelled, "The budget account or one of its ancestors was cancelled.");
        return [.. ancestors.Select(account => new BudgetAccountId(account.AccountId))];
    }

    /// <summary>Ancestors for a new leaf: the declared parent must exist and its stored chain must be consistent.</summary>
    private static ImmutableArray<BudgetAccountId> ParentAncestors(
        Dictionary<string, ExecutionBudgetAccountEntity> accounts, ExecutionBudgetAccountEntity parent,
        BudgetAccountDefinition expected)
    {
        var chain = new List<ExecutionBudgetAccountEntity>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = parent;
        while (true)
        {
            if (!visited.Add(current.AccountId))
                throw Error(BudgetErrorCodes.PathInvalid, "The budget account parent chain is cyclic or self-referencing.");
            chain.Add(current);
            if (chain.Count + 1 > MaxAccountPathDepth)
                throw Error(BudgetErrorCodes.PathInvalid, "The budget account path exceeds the maximum depth.");
            if (current.ParentAccountId is not { } ancestorId) break;
            if (current.RootKind is (int)BudgetAccountKind.GoalRun or (int)BudgetAccountKind.Task)
                throw Error(BudgetErrorCodes.PathInvalid, "GoalRun and Task accounts can only be roots.");
            if (!accounts.TryGetValue(ancestorId, out var ancestor))
                throw Error(BudgetErrorCodes.PathInvalid, "The budget account ancestor chain is broken.");
            current = ancestor;
        }
        chain.Reverse();
        // A new leaf must match the parent chain in its own requested currency.
        chain.ForEach(ancestor => ValidateAncestor(expected, expected.CostUnit, ancestor));
        if (chain.Exists(ancestor => ancestor.Cancelled))
            throw Error(BudgetErrorCodes.Cancelled, "The budget account ancestor was cancelled.");
        return [.. chain.Select(account => new BudgetAccountId(account.AccountId))];
    }

    /// <summary>Ancestors and descendants must share Workspace, Mode and CostUnit; delegation may change Agent/Session.</summary>
    private static void ValidateAncestor(BudgetAccountDefinition expected, string chainCurrency,
        ExecutionBudgetAccountEntity ancestor)
    {
        if (!string.Equals(expected.Identity.WorkspaceId, ancestor.WorkspaceId, StringComparison.Ordinal)
            || (int)expected.Mode != ancestor.EnforcementMode)
            throw Error(BudgetErrorCodes.Conflict,
                "The budget account ancestor workspace or enforcement mode differs from the requested definition.");
        if (!string.Equals(chainCurrency, ancestor.CostUnit, StringComparison.Ordinal))
            throw Error(BudgetErrorCodes.CurrencyMismatch,
                "The budget account ancestor cost unit differs from the currency that defines the chain.");
    }

    /// <summary>Every persisted field participates: a different definition is a conflict, never a silent reuse or widening.</summary>
    private static void EnsureSameDefinition(BudgetAccountDefinition expected, BudgetAccountDefinition actual)
    {
        if (!string.Equals(expected.AccountId.Value, actual.AccountId.Value, StringComparison.Ordinal)
            || !SameId(expected.ParentAccountId, actual.ParentAccountId)
            || !SameIdentity(expected.Identity, actual.Identity)
            || !SameLimits(expected.Limits, actual.Limits)
            || !string.Equals(expected.CostUnit, actual.CostUnit, StringComparison.Ordinal)
            || expected.Mode != actual.Mode)
            throw Error(BudgetErrorCodes.Conflict,
                "The existing budget account differs from the requested definition; it is never widened or silently reused.");
    }

    private static bool SameId(BudgetAccountId? left, BudgetAccountId? right) =>
        left is null ? right is null
            : right is not null && string.Equals(left.Value, right.Value, StringComparison.Ordinal);

    private static bool SameIdentity(BudgetIdentity left, BudgetIdentity right) =>
        left.RootKind == right.RootKind
        && string.Equals(left.RootId, right.RootId, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)
        && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal);

    private static bool SameLimits(BudgetLimits left, BudgetLimits right) =>
        left.MaxOutputTokens == right.MaxOutputTokens
        && left.MaxCost == right.MaxCost
        && left.MaxCalls == right.MaxCalls
        && left.MaxConcurrentAttempts == right.MaxConcurrentAttempts
        && left.MaxInputTokensPerRequest == right.MaxInputTokensPerRequest;

    private static BudgetAccountDefinition Definition(ExecutionBudgetAccountEntity account) => new(
        new BudgetAccountId(account.AccountId),
        account.ParentAccountId is null ? null : new BudgetAccountId(account.ParentAccountId),
        new BudgetIdentity((BudgetAccountKind)account.RootKind, account.RootId, account.WorkspaceId, account.AgentId,
            account.SessionId),
        new BudgetLimits(account.MaxOutputTokens, account.MaxCost is null ? null : ParseCost(account.MaxCost),
            account.MaxCalls, account.MaxConcurrentAttempts, account.MaxInputTokensPerRequest),
        account.CostUnit,
        (BudgetEnforcementMode)account.EnforcementMode);

    private static void ValidateDefinition(BudgetAccountDefinition expected)
    {
        if (expected is null)
            throw Error(BudgetErrorCodes.AccountInvalid, "A budget account definition is required.");
        if (!BoundedAccountId(expected.AccountId))
            throw Error(BudgetErrorCodes.AccountInvalid, "A bounded host-owned account id is required.");
        if (expected.Identity is null || !expected.Identity.IsValid)
            throw Error(BudgetErrorCodes.AccountInvalid, "A valid budget identity is required.");
        if (!BoundedId(expected.Identity.RootId))
            throw Error(BudgetErrorCodes.AccountInvalid, "The budget root id is not a bounded account id.");
        if (expected.Limits is null || !ValidLimits(expected.Limits))
            throw Error(BudgetErrorCodes.AccountInvalid, "Valid budget limits are required.");
        if (!Enum.IsDefined(expected.Mode))
            throw Error(BudgetErrorCodes.AccountInvalid, "A defined enforcement mode is required.");
        if (string.IsNullOrWhiteSpace(expected.CostUnit) || expected.CostUnit.Length > 32)
            throw Error(BudgetErrorCodes.AccountInvalid, "A bounded cost unit is required.");
        if (expected.ParentAccountId is { } parent && !BoundedAccountId(parent))
            throw Error(BudgetErrorCodes.AccountInvalid, "A bounded parent account id is required.");
        if (expected.Identity.RootKind is BudgetAccountKind.GoalRun or BudgetAccountKind.Task
            && expected.ParentAccountId is not null)
            throw Error(BudgetErrorCodes.AccountInvalid, "GoalRun and Task accounts can only be roots.");
        if (!string.Equals(DeterministicAccountId(expected.Identity.RootKind, expected.Identity.RootId).Value,
                expected.AccountId.Value, StringComparison.Ordinal))
            throw Error(BudgetErrorCodes.AccountInvalid, "The account id does not match its host-bound identity kind/id.");
    }

    private static BudgetAccountId DeterministicAccountId(BudgetAccountKind kind, string rootId) => kind switch
    {
        BudgetAccountKind.GoalRun => new BudgetAccountId("goal:" + rootId),
        BudgetAccountKind.Work => new BudgetAccountId("work:" + rootId),
        BudgetAccountKind.Task => new BudgetAccountId("task:" + rootId),
        _ => throw Error(BudgetErrorCodes.AccountInvalid, "The account kind is not supported."),
    };

    private static bool BoundedAccountId(BudgetAccountId? account) => account is { IsValid: true } && BoundedId(account.Value);

    /// <summary>No comma (the reservation chain separator), no control characters and no leading/trailing whitespace.</summary>
    private static bool BoundedId(string? value) => value is { Length: > 0 and <= 128 }
        && value == value.Trim() && value.IndexOf(',') < 0 && !value.Any(char.IsControl);

    private static bool IsSqliteContention(Exception? exception) => Contention(exception, 0);

    private static bool Contention(Exception? exception, int depth) => exception switch
    {
        null => false,
        SqliteException sqlite =>
            sqlite.SqliteErrorCode is 5 or 6
            || (sqlite.SqliteErrorCode == 19 && sqlite.SqliteExtendedErrorCode is 1555 or 2067),
        _ when depth < 4 => Contention(exception.InnerException, depth + 1),
        _ => false,
    };
}
