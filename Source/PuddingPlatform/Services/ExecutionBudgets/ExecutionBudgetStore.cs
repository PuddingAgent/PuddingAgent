using System.Collections.Immutable;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// PA-01 S5 product ledger: one SQLite ledger per host root. The port semantics match the independent
/// in-memory ledger exactly — ancestor-atomic reservations, operation/attempt idempotency, cancellation
/// linearized before dispatch, owner fencing, idempotent settlement, unknown retention (never refunded).
/// The whole account tree is small; each mutation loads it inside one serialized SQLite transaction.
/// </summary>
public sealed partial class ExecutionBudgetStore(IDbContextFactory<PlatformDbContext> dbFactory) : IExecutionBudgetRecoveryLedger
{
    private const int Pending = 0;
    private const int Dispatched = 1;
    private const int Unknown = 2;
    private const int Settled = 3;
    private const int Released = 4;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BudgetAccountId> CreateAccountAsync(BudgetAccountId account, BudgetAccountId? parent,
        BudgetLimits limits, BudgetIdentity identity, string costUnit,
        BudgetEnforcementMode mode = BudgetEnforcementMode.Legacy, CancellationToken ct = default)
    {
        if (account is null || !account.IsValid || identity is null || !identity.IsValid || limits is null
            || !ValidLimits(limits) || string.IsNullOrWhiteSpace(costUnit) || costUnit.Length > 32)
            throw Error(BudgetErrorCodes.AccountInvalid, "A host-bound account, identity, limits and cost unit are required.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (await db.ExecutionBudgetAccounts.AnyAsync(a => a.AccountId == account.Value, ct).ConfigureAwait(false))
                throw Error(BudgetErrorCodes.AccountExists, "The budget account already exists.");
            if (parent is not null && !await db.ExecutionBudgetAccounts.AnyAsync(
                    a => a.AccountId == parent.Value, ct).ConfigureAwait(false))
                throw Error(BudgetErrorCodes.AccountMissing, "The parent budget account is missing.");
            db.ExecutionBudgetAccounts.Add(new ExecutionBudgetAccountEntity
            {
                AccountId = account.Value,
                ParentAccountId = parent?.Value,
                RootKind = (int)identity.RootKind,
                RootId = identity.RootId,
                WorkspaceId = identity.WorkspaceId,
                AgentId = identity.AgentId,
                SessionId = identity.SessionId,
                CostUnit = costUnit,
                MaxOutputTokens = limits.MaxOutputTokens,
                MaxCost = limits.MaxCost?.ToString("G29", CultureInfo.InvariantCulture),
                MaxCalls = limits.MaxCalls,
                MaxConcurrentAttempts = limits.MaxConcurrentAttempts,
                MaxInputTokensPerRequest = limits.MaxInputTokensPerRequest,
                EnforcementMode = (int)mode,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return account;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Trusted identity of an existing account (host-bound; never taken from request fields).</summary>
    public async Task<(BudgetIdentity Identity, string CostUnit)> GetAccountIdentityAsync(BudgetAccountId account,
        CancellationToken ct = default)
    {
        if (account is null || !account.IsValid)
            throw Error(BudgetErrorCodes.AccountInvalid, "A valid budget account id is required.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await db.ExecutionBudgetAccounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.AccountId == account.Value, ct).ConfigureAwait(false)
                ?? throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            return (new((BudgetAccountKind)entity.RootKind, entity.RootId, entity.WorkspaceId, entity.AgentId,
                entity.SessionId), entity.CostUnit);
        }
        finally { _gate.Release(); }
    }
    /// <summary>Reads the root enforcement mode.</summary>
    public async Task<BudgetEnforcementMode> GetEnforcementModeAsync(BudgetAccountId account,
        CancellationToken ct = default)
    {
        if (account is null || !account.IsValid)
            throw Error(BudgetErrorCodes.AccountInvalid, "A valid budget account id is required.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await db.ExecutionBudgetAccounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.AccountId == account.Value, ct).ConfigureAwait(false)
                ?? throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            return (BudgetEnforcementMode)entity.EnforcementMode;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Only a Legacy/Shadow root may be promoted; once Enforced it can never be downgraded in place.</summary>
    public async Task<BudgetEnforcementMode> SetEnforcementModeAsync(BudgetAccountId account,
        BudgetEnforcementMode mode, CancellationToken ct = default)
    {
        if (account is null || !account.IsValid || mode is not (BudgetEnforcementMode.Legacy
            or BudgetEnforcementMode.Shadow or BudgetEnforcementMode.Enforced))
            throw Error(BudgetErrorCodes.AccountInvalid, "A valid account and enforcement mode are required.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await db.ExecutionBudgetAccounts
                .SingleOrDefaultAsync(a => a.AccountId == account.Value, ct).ConfigureAwait(false)
                ?? throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            if (entity.EnforcementMode == (int)BudgetEnforcementMode.Enforced
                && mode != BudgetEnforcementMode.Enforced)
                throw Error(BudgetErrorCodes.Conflict, "An enforced root cannot be downgraded in place.");
            entity.EnforcementMode = (int)mode;
            entity.Revision++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return mode;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<BudgetReservation> ReserveAsync(ReserveBudget command, CancellationToken ct = default)
    {
        ValidateReserve(command);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
            var fingerprint = Fingerprint(command);
            var prior = await db.ExecutionBudgetReservations.AsNoTracking()
                .SingleOrDefaultAsync(r => r.OperationId == command.OperationId, ct).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.AttemptId == command.AttemptId)
                {
                    if (prior.Fingerprint != fingerprint)
                        throw Error(BudgetErrorCodes.Conflict, "The operation id was already used with different budget input.");
                    if (prior.State == Released)
                        throw Error(BudgetErrorCodes.Conflict, "A released attempt must be retried with a new attempt id.");
                    return ToReservation(prior);
                }
                if (prior.State is Pending or Dispatched or Unknown)
                    throw Error(BudgetErrorCodes.Conflict, "The operation already has an in-flight attempt; unknown is not retried automatically.");
            }
            if (await db.ExecutionBudgetReservations.AnyAsync(r => r.AttemptId == command.AttemptId, ct).ConfigureAwait(false))
                throw Error(BudgetErrorCodes.Conflict, "The attempt id is already bound to another budget operation.");
            var chain = ResolveChain(accounts, command.AccountId);
            if (command.AccountPath.IsDefaultOrEmpty || command.AccountPath.Length != chain.Count
                || !command.AccountPath.SequenceEqual(chain.Select(a => new BudgetAccountId(a.AccountId))))
                throw Error(BudgetErrorCodes.PathInvalid, "The account path does not match the host-owned ancestry.");
            foreach (var account in chain)
            {
                if (account.Cancelled) throw Error(BudgetErrorCodes.Cancelled, "The budget root was cancelled; no new attempt is admitted.");
                // Unknown usage is an accounting state, not a terminal failure: a retry may still be admitted within limits.
                if (account.FailureCode is { } failure && failure != BudgetErrorCodes.UsageUnknown)
                    throw Error(failure, "The budget root is not available for further attempts.");
                if (!string.Equals(account.CostUnit, command.CostUnit, StringComparison.Ordinal))
                    throw Error(BudgetErrorCodes.CurrencyMismatch, "The attempt cost unit differs from the account unit.");
                if (account.MaxCalls is { } calls && account.Calls >= calls
                    || account.MaxOutputTokens is { } output
                    && (decimal)account.OutputTokens + account.UnknownOutputTokens + account.ReservedOutputTokens + command.MaxOutputTokens > output
                    || MaxCostLimit(account) is { } money
                    && Cost(account) + UnknownCost(account) + ReservedCost(account) + command.MaxCost > money)
                    throw Error(BudgetErrorCodes.Exhausted, "The budget is exhausted.");
                if (account.ActiveAttempts >= account.MaxConcurrentAttempts)
                    throw Error(BudgetErrorCodes.Concurrency, "The budget concurrency is occupied.");
                if (account.MaxInputTokensPerRequest is { } capacity && command.MaxInputTokens > capacity)
                    throw Error(BudgetErrorCodes.InputCapacity, "The request input capacity exceeds the account limit.");
            }
            var reservation = new ExecutionBudgetReservationEntity
            {
                ReservationId = Guid.NewGuid().ToString("N"),
                AccountId = chain[^1].AccountId,
                RootAccountId = chain[0].AccountId,
                AttemptId = command.AttemptId,
                OperationId = command.OperationId,
                Fingerprint = fingerprint,
                Chain = string.Join(',', chain.Select(a => a.AccountId)),
                CostUnit = command.CostUnit,
                MaxInputTokens = command.MaxInputTokens,
                ReservedOutputTokens = command.MaxOutputTokens,
                ReservedCost = command.MaxCost.ToString("G29", CultureInfo.InvariantCulture),
                State = Pending,
            };
            db.ExecutionBudgetReservations.Add(reservation);
            foreach (var account in chain)
            {
                account.Calls++;
                account.ActiveAttempts++;
                account.ReservedOutputTokens += command.MaxOutputTokens;
                account.ReservedCost = (ReservedCost(account) + command.MaxCost).ToString("G29", CultureInfo.InvariantCulture);
                account.Revision++;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToReservation(reservation);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<DispatchPermit> MarkDispatchedAsync(DispatchBudget command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var reservation = await FindAsync(db, command.ReservationId, command.AttemptId, ct).ConfigureAwait(false);
            if (reservation.State != Pending) throw Error(BudgetErrorCodes.DispatchInvalid, "Only a pending reservation can be dispatched.");
            if (command.OwnerEpoch < 1) throw Error(BudgetErrorCodes.RequestInvalid, "A owner epoch is required.");
            var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
            var chain = Chain(reservation, accounts);
            foreach (var account in chain)
            {
                if (account.Cancelled || account.CancellationRevision != command.ExpectedCancellationRevision)
                {
                    if (account.Cancelled) throw Error(BudgetErrorCodes.Cancelled, "Cancellation was recorded before the dispatch linearization point.");
                    throw Error(BudgetErrorCodes.Conflict, "The caller observed a stale cancellation revision.");
                }
            }
            reservation.State = Dispatched;
            reservation.OwnerEpoch = command.OwnerEpoch;
            foreach (var account in chain) account.Revision++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new DispatchPermit(reservation.ReservationId, reservation.AttemptId, command.OwnerEpoch,
                chain[^1].CancellationRevision);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<SettlementReceipt> SettleAsync(SettleBudget command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var reservation = await FindAsync(db, command.ReservationId, command.AttemptId, ct).ConfigureAwait(false);
            if (reservation.State == Settled)
            {
                if (reservation.ReceiptFingerprint == Fingerprint(command))
                    return new SettlementReceipt(reservation.ReservationId, reservation.AttemptId,
                        reservation.ReceiptRevision ?? 0, command.Usage, Cost(reservation), reservation.CostUnit,
                        reservation.ReceiptOverrun ?? false, reservation.ReceiptFromUnknown ?? false);
                throw Error(BudgetErrorCodes.SettlementConflict, "The attempt was already settled with a different receipt.");
            }
            if (reservation.State == Pending) throw Error(BudgetErrorCodes.SettlementInvalid, "An undispatched attempt cannot be settled.");
            if (command.OwnerEpoch < reservation.OwnerEpoch) throw Error(BudgetErrorCodes.OwnerStale, "A stale owner cannot settle this attempt.");
            if (!string.Equals(reservation.CostUnit, command.CostUnit, StringComparison.Ordinal))
                throw Error(BudgetErrorCodes.CurrencyMismatch, "The settlement cost unit differs from the reservation unit.");
            if (command.Usage is null) throw Error(BudgetErrorCodes.UsageInvalid, "Actual provider usage is required.");
            if (command.ActualCost < 0) throw Error(BudgetErrorCodes.RequestInvalid, "Actual cost cannot be negative.");
            if (command.Usage.InputTokens < 0 || command.Usage.OutputTokens < 0 || command.Usage.CacheHitTokens < 0)
                throw Error(BudgetErrorCodes.UsageInvalid, "Actual provider usage is invalid.");
            var fromUnknown = reservation.State == Unknown;
            var overrun = command.Usage.InputTokens > reservation.MaxInputTokens
                || command.Usage.OutputTokens > reservation.ReservedOutputTokens
                || command.ActualCost > ReservedCost(reservation);
            var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
            var chain = Chain(reservation, accounts);
            foreach (var account in chain)
            {
                if (fromUnknown)
                {
                    account.UnknownOutputTokens = Math.Max(0, account.UnknownOutputTokens - reservation.ReservedOutputTokens);
                    account.UnknownCost = Math.Max(0, UnknownCost(account) - ReservedCost(reservation))
                        .ToString("G29", CultureInfo.InvariantCulture);
                    if (account.UnknownOutputTokens == 0 && UnknownCost(account) == 0 && account.FailureCode == BudgetErrorCodes.UsageUnknown)
                        account.FailureCode = null;
                }
                else
                {
                    account.ReservedOutputTokens = Math.Max(0, account.ReservedOutputTokens - reservation.ReservedOutputTokens);
                    account.ReservedCost = Math.Max(0, ReservedCost(account) - ReservedCost(reservation))
                        .ToString("G29", CultureInfo.InvariantCulture);
                }
                account.ActiveAttempts = Math.Max(0, account.ActiveAttempts - 1);
                account.InputTokens += command.Usage.InputTokens;
                account.OutputTokens += command.Usage.OutputTokens;
                account.CacheHitTokens += command.Usage.CacheHitTokens;
                account.Cost = (Cost(account) + command.ActualCost).ToString("G29", CultureInfo.InvariantCulture);
                if (overrun) account.FailureCode = BudgetErrorCodes.ProviderOverrun;
                account.Revision++;
            }
            reservation.State = Settled;
            reservation.ReceiptInputTokens = command.Usage.InputTokens;
            reservation.ReceiptOutputTokens = command.Usage.OutputTokens;
            reservation.ReceiptCacheHitTokens = command.Usage.CacheHitTokens;
            reservation.ReceiptCost = command.ActualCost.ToString("G29", CultureInfo.InvariantCulture);
            reservation.ReceiptCostUnit = command.CostUnit;
            reservation.ReceiptOverrun = overrun;
            reservation.ReceiptFromUnknown = fromUnknown;
            reservation.ReceiptFingerprint = Fingerprint(command);
            reservation.ReceiptRevision = chain[^1].Revision;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new SettlementReceipt(reservation.ReservationId, reservation.AttemptId, chain[^1].Revision,
                command.Usage, command.ActualCost, command.CostUnit, overrun, fromUnknown);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<BudgetSnapshot> MarkUnknownAsync(MarkUnknownBudget command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var reservation = await FindAsync(db, command.ReservationId, command.AttemptId, ct).ConfigureAwait(false);
            if (reservation.State == Pending) throw Error(BudgetErrorCodes.DispatchInvalid, "An undispatched attempt cannot become unknown.");
            if (reservation.State == Settled) throw Error(BudgetErrorCodes.UnknownStale, "A settled attempt is not unknown.");
            if (command.OwnerEpoch < reservation.OwnerEpoch) throw Error(BudgetErrorCodes.OwnerStale, "A stale owner cannot mark this attempt unknown.");
            var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
            var chain = Chain(reservation, accounts);
            if (reservation.State == Dispatched)
            {
                foreach (var account in chain)
                {
                    account.ReservedOutputTokens = Math.Max(0, account.ReservedOutputTokens - reservation.ReservedOutputTokens);
                    account.ReservedCost = Math.Max(0, ReservedCost(account) - ReservedCost(reservation))
                        .ToString("G29", CultureInfo.InvariantCulture);
                    account.UnknownOutputTokens += reservation.ReservedOutputTokens;
                    account.UnknownCost = (UnknownCost(account) + ReservedCost(reservation))
                        .ToString("G29", CultureInfo.InvariantCulture);
                    account.FailureCode ??= BudgetErrorCodes.UsageUnknown;
                    account.ActiveAttempts = Math.Max(0, account.ActiveAttempts - 1);
                    account.Revision++;
                }
                reservation.State = Unknown;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            return Snapshot(accounts[chain[^1].AccountId]);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<BudgetSnapshot> ReleaseAsync(ReleaseBudget command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var reservation = await FindAsync(db, command.ReservationId, command.AttemptId, ct).ConfigureAwait(false);
            if (reservation.State != Pending)
                throw Error(BudgetErrorCodes.DispatchInvalid, "Only an undispatched reservation can be released.");
            var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
            var chain = Chain(reservation, accounts);
            foreach (var account in chain)
            {
                account.ReservedOutputTokens = Math.Max(0, account.ReservedOutputTokens - reservation.ReservedOutputTokens);
                account.ReservedCost = Math.Max(0, ReservedCost(account) - ReservedCost(reservation))
                    .ToString("G29", CultureInfo.InvariantCulture);
                account.ActiveAttempts = Math.Max(0, account.ActiveAttempts - 1);
                account.Revision++;
            }
            reservation.State = Released;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Snapshot(accounts[chain[^1].AccountId]);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CancelBudgetReceipt> CancelAsync(CancelBudget command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
            if (command.AccountId is null || !accounts.TryGetValue(command.AccountId.Value, out var root))
                throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            if (string.IsNullOrWhiteSpace(command.ReasonCode) || command.ReasonCode.Length > 128)
                throw Error(BudgetErrorCodes.RequestInvalid, "A cancellation reason is required.");
            var alreadyCancelled = root.Cancelled;
            var subtree = Subtree(accounts, root).ToList();
            var ids = subtree.Select(a => a.AccountId).ToHashSet(StringComparer.Ordinal);
            foreach (var account in subtree)
            {
                if (account.Cancelled) continue;
                account.Cancelled = true;
                account.ReasonCode = command.ReasonCode;
                account.Revision++;
                account.CancellationRevision = account.Revision;
            }
            var reservationRows = await db.ExecutionBudgetReservations.AsNoTracking()
                .Where(r => ids.Contains(r.AccountId)).ToListAsync(ct).ConfigureAwait(false);
            var running = reservationRows.Count(r => r.State is Dispatched or Unknown);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new CancelBudgetReceipt(new BudgetAccountId(root.AccountId), root.CancellationRevision, running,
                alreadyCancelled);
        }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableArray<BudgetAccountId>> ListAccountIdsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await db.ExecutionBudgetAccounts.AsNoTracking().Select(a => a.AccountId).ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. ids.OrderBy(id => id, StringComparer.Ordinal).Select(id => new BudgetAccountId(id))];
    }
    public async ValueTask<BudgetSnapshot> ReadAsync(BudgetAccountId account, CancellationToken ct = default)
    {
        if (account is null || !account.IsValid) throw Error(BudgetErrorCodes.RequestInvalid, "A bounded account id is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ExecutionBudgetAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.AccountId == account.Value, ct).ConfigureAwait(false);
        return entity is null
            ? throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.")
            : Snapshot(entity);
    }

    public async ValueTask<ImmutableArray<BudgetRecoveryCandidate>> ListUnsettledAsync(BudgetAccountId account,
        CancellationToken ct = default)
    {
        if (account is null || !account.IsValid) throw Error(BudgetErrorCodes.RequestInvalid, "A bounded account id is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var accounts = await LoadAccountsAsync(db, ct).ConfigureAwait(false);
        if (!accounts.TryGetValue(account.Value, out var root))
            throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
        var leafIds = Subtree(accounts, root).Select(a => a.AccountId).ToHashSet(StringComparer.Ordinal);
        var rows = await db.ExecutionBudgetReservations.AsNoTracking()
            .Where(r => leafIds.Contains(r.AccountId) && (r.State == Pending || r.State == Dispatched || r.State == Unknown))
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new BudgetRecoveryCandidate(r.ReservationId, r.AttemptId, r.OwnerEpoch,
            r.State switch { Pending => BudgetAttemptPhase.Pending, Dispatched => BudgetAttemptPhase.Dispatched, _ => BudgetAttemptPhase.Unknown }))];
    }

    private static async Task<Dictionary<string, ExecutionBudgetAccountEntity>> LoadAccountsAsync(
        PlatformDbContext db, CancellationToken ct)
        => await db.ExecutionBudgetAccounts.ToDictionaryAsync(a => a.AccountId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

    private static List<ExecutionBudgetAccountEntity> Chain(ExecutionBudgetReservationEntity reservation,
        Dictionary<string, ExecutionBudgetAccountEntity> accounts)
        => [.. reservation.Chain.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => accounts.TryGetValue(id, out var account)
                ? account
                : throw Error(BudgetErrorCodes.AccountMissing, "The budget account chain is broken."))];

    private static List<ExecutionBudgetAccountEntity> ResolveChain(
        Dictionary<string, ExecutionBudgetAccountEntity> accounts, BudgetAccountId accountId)
    {
        if (accountId is null || !accounts.TryGetValue(accountId.Value, out var leaf))
            throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
        var chain = new List<ExecutionBudgetAccountEntity>();
        for (var current = leaf; current is not null;)
        {
            chain.Add(current);
            if (chain.Count > 64) throw Error(BudgetErrorCodes.AccountInvalid, "The budget account depth is unbounded.");
            current = current.ParentAccountId is { } parent ? accounts.GetValueOrDefault(parent) : null;
        }
        chain.Reverse();
        return chain;
    }

    private static IEnumerable<ExecutionBudgetAccountEntity> Subtree(
        Dictionary<string, ExecutionBudgetAccountEntity> accounts, ExecutionBudgetAccountEntity root)
    {
        yield return root;
        foreach (var child in accounts.Values.Where(a => a.ParentAccountId == root.AccountId).ToList())
            foreach (var nested in Subtree(accounts, child)) yield return nested;
    }

    private static async Task<ExecutionBudgetReservationEntity> FindAsync(PlatformDbContext db,
        string reservationId, string attemptId, CancellationToken ct)
    {
        var reservation = await db.ExecutionBudgetReservations.SingleOrDefaultAsync(
            r => r.ReservationId == reservationId, ct).ConfigureAwait(false);
        if (reservation is null || reservation.AttemptId != attemptId)
            throw Error(BudgetErrorCodes.SettlementInvalid, "The reservation does not belong to this attempt.");
        return reservation;
    }

    private static BudgetReservation ToReservation(ExecutionBudgetReservationEntity r)
        => new(r.ReservationId, new BudgetAccountId(r.AccountId), new BudgetAccountId(r.RootAccountId), r.AttemptId, 1,
            r.OwnerEpoch, r.ReservedOutputTokens, ReservedCost(r), r.CostUnit);

    private static decimal ParseCost(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static BudgetSnapshot Snapshot(ExecutionBudgetAccountEntity a) => new(new BudgetAccountId(a.AccountId),
        a.Revision, new BudgetLimits(a.MaxOutputTokens, a.MaxCost is null ? null : ParseCost(a.MaxCost),
            a.MaxCalls, a.MaxConcurrentAttempts, a.MaxInputTokensPerRequest),
        a.InputTokens, a.OutputTokens, a.CacheHitTokens, Cost(a), a.CostUnit, a.ReservedOutputTokens, ReservedCost(a),
        a.UnknownOutputTokens, UnknownCost(a), a.Calls, a.ActiveAttempts, a.Cancelled, a.CancellationRevision,
        a.ReasonCode ?? a.FailureCode);

    private static decimal? MaxCostLimit(ExecutionBudgetAccountEntity a) => a.MaxCost is null ? null : ParseCost(a.MaxCost);
    private static decimal Cost(ExecutionBudgetAccountEntity a) => ParseCost(a.Cost);
    private static decimal ReservedCost(ExecutionBudgetAccountEntity a) => ParseCost(a.ReservedCost);
    private static decimal UnknownCost(ExecutionBudgetAccountEntity a) => ParseCost(a.UnknownCost);
    private static decimal Cost(ExecutionBudgetReservationEntity r) => ParseCost(r.ReceiptCost ?? "0");
    private static decimal ReservedCost(ExecutionBudgetReservationEntity r) => ParseCost(r.ReservedCost);

    private static void ValidateReserve(ReserveBudget command)
    {
        if (command is null) throw Error(BudgetErrorCodes.RequestInvalid, "A reserve command is required.");
        if (!Identifier(command.OperationId) || !Identifier(command.AttemptId))
            throw Error(BudgetErrorCodes.RequestInvalid, "Operation and attempt ids are required.");
        if (command.AccountId is null || !command.AccountId.IsValid || command.Identity is null || !command.Identity.IsValid)
            throw Error(BudgetErrorCodes.RequestInvalid, "A host-bound account and identity are required.");
        if (command.AccountPath.IsDefaultOrEmpty) throw Error(BudgetErrorCodes.PathInvalid, "The host-owned account path is required.");
        if (!Identifier(command.ModelKey) || !Identifier(command.ModelRevision) || !Identifier(command.PriceVersion))
            throw Error(BudgetErrorCodes.RequestInvalid, "A frozen model and price revision are required.");
        if (command.MaxInputTokens is < 1 or > 10_000_000 || command.MaxOutputTokens is < 1 or > 1_000_000 || command.MaxCost < 0)
            throw Error(BudgetErrorCodes.RequestInvalid, "The reserved bounds are invalid.");
        if (string.IsNullOrWhiteSpace(command.CostUnit) || command.CostUnit.Length > 32)
            throw Error(BudgetErrorCodes.RequestInvalid, "A cost unit is required.");
        if (command.Deadline <= DateTimeOffset.UtcNow || command.Deadline > DateTimeOffset.UtcNow.AddDays(7))
            throw Error(BudgetErrorCodes.RequestInvalid, "A bounded future deadline is required.");
    }

    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static bool ValidLimits(BudgetLimits limits)
        => limits.MaxOutputTokens is null or >= 0 && limits.MaxCost is null or >= 0 && limits.MaxCalls is null or >= 0
           && limits.MaxConcurrentAttempts is >= 1 and <= 64 && limits.MaxInputTokensPerRequest is null or >= 0;

    private static string Fingerprint(ReserveBudget c) => string.Join('|', c.OperationId, c.AttemptId, c.AccountId.Value,
        string.Join(',', c.AccountPath.Select(a => a.Value)), c.Identity.RootKind, c.Identity.RootId,
        c.Identity.WorkspaceId, c.Identity.AgentId, c.Identity.SessionId, c.ModelKey, c.ModelRevision, c.PriceVersion,
        c.MaxInputTokens, c.MaxOutputTokens, c.MaxCost.ToString("G29", CultureInfo.InvariantCulture), c.CostUnit);

    private static string Fingerprint(SettleBudget c) => string.Join('|', c.AttemptId, c.Usage.InputTokens,
        c.Usage.OutputTokens, c.Usage.CacheHitTokens, c.ActualCost.ToString("G29", CultureInfo.InvariantCulture),
        c.CostUnit, c.ProviderReceipt);

    private static ExecutionResourceException Error(string code, string message) => new(code, message);
}
