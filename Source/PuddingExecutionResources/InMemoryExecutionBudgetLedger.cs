using System.Collections.Immutable;
using PuddingExecutionResources.Contracts;

namespace PuddingExecutionResources;

/// <summary>
/// Independent-test ledger for the durable budget ports: one lock and one revision chain per account tree.
/// It implements the same invariants the persistent ledger must uphold: ancestor-atomic reservations,
/// cancellation linearized before dispatch, owner fencing, idempotent settlement, unknown retention.
/// The product must wire exactly one persistent ledger; this class is not a second production ledger.
/// </summary>
public sealed class InMemoryExecutionBudgetLedger : IExecutionBudgetRecoveryLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reservation> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, Reservation Reservation)> _operations = new(StringComparer.Ordinal);

    public BudgetAccountId CreateAccount(BudgetAccountId account, BudgetAccountId? parent, BudgetLimits limits,
        BudgetIdentity identity, string costUnit)
    {
        lock (_gate)
        {
            if (account is null || !account.IsValid) throw Error(BudgetErrorCodes.AccountInvalid, "A bounded account id is required.");
            if (identity is null || !identity.IsValid) throw Error(BudgetErrorCodes.AccountInvalid, "A host-bound budget identity is required.");
            if (limits is null || !ValidLimits(limits)) throw Error(BudgetErrorCodes.AccountInvalid, "Budget limits are invalid.");
            if (string.IsNullOrWhiteSpace(costUnit) || costUnit.Length > 32) throw Error(BudgetErrorCodes.AccountInvalid, "A cost unit is required.");
            if (_accounts.ContainsKey(account.Value)) throw Error(BudgetErrorCodes.AccountExists, "The budget account already exists.");
            Account? parentAccount = null;
            if (parent is not null)
            {
                if (!_accounts.TryGetValue(parent.Value, out parentAccount)) throw Error(BudgetErrorCodes.AccountMissing, "The parent budget account is missing.");
            }
            var created = new Account(account, parentAccount, limits, identity, costUnit);
            _accounts.Add(account.Value, created);
            return account;
        }
    }

    public ValueTask<BudgetReservation> ReserveAsync(ReserveBudget command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ValidateReserve(command);
            var fingerprint = Fingerprint(command);
            if (_operations.TryGetValue(command.OperationId, out var prior))
            {
                if (prior.Reservation.AttemptId == command.AttemptId)
                {
                    // Replaying the same attempt with the same body is accepted once; a different body is a conflict.
                    if (prior.Fingerprint != fingerprint)
                        throw Error(BudgetErrorCodes.Conflict, "The operation id was already used with different budget input.");
                    if (prior.Reservation.State == AttemptState.Released)
                        throw Error(BudgetErrorCodes.Conflict, "A released attempt must be retried with a new attempt id.");
                    return ValueTask.FromResult(prior.Reservation.ToContract());
                }
                // A retry keeps the business operation id but must not duplicate an in-flight or unknown attempt.
                if (prior.Reservation.State is AttemptState.Pending or AttemptState.Dispatched or AttemptState.Unknown)
                    throw Error(BudgetErrorCodes.Conflict, "The operation already has an in-flight attempt; unknown is not retried automatically.");
            }
            if (_attempts.ContainsKey(command.AttemptId)) throw Error(BudgetErrorCodes.Conflict, "The attempt id is already bound to another budget operation.");
            var chain = ResolveChain(command.AccountId);
            if (command.AccountPath.IsDefaultOrEmpty || command.AccountPath.Length != chain.Count
                || !command.AccountPath.SequenceEqual(chain.Select(a => a.Id)))
                throw Error(BudgetErrorCodes.PathInvalid, "The account path does not match the host-owned ancestry.");
            foreach (var account in chain)
            {
                if (account.Cancelled) throw Error(BudgetErrorCodes.Cancelled, "The budget root was cancelled; no new attempt is admitted.");
                if (account.FailureCode is { } failure) throw Error(failure, "The budget root is not available for further attempts.");
                if (!string.Equals(account.CostUnit, command.CostUnit, StringComparison.Ordinal))
                    throw Error(BudgetErrorCodes.CurrencyMismatch, "The attempt cost unit differs from the account unit.");
                if (account.Limits.MaxCalls is { } calls && account.Calls >= calls
                    || account.Limits.MaxOutputTokens is { } output
                    && (decimal)account.OutputTokens + account.UnknownOutputTokens + account.ReservedOutputTokens + command.MaxOutputTokens > output
                    || account.Limits.MaxCost is { } money
                    && account.Cost + account.UnknownCost + account.ReservedCost + command.MaxCost > money)
                    throw Error(BudgetErrorCodes.Exhausted, "The budget is exhausted.");
                if (account.ActiveAttempts >= account.Limits.MaxConcurrentAttempts)
                    throw Error(BudgetErrorCodes.Concurrency, "The budget concurrency is occupied.");
                if (account.Limits.MaxInputTokensPerRequest is { } capacity && command.MaxInputTokens > capacity)
                    throw Error(BudgetErrorCodes.InputCapacity, "The request input capacity exceeds the account limit.");
            }
            var reservation = new Reservation(NewId(), command, chain);
            foreach (var account in chain)
            {
                account.Calls++;
                account.ActiveAttempts++;
                account.ReservedOutputTokens += command.MaxOutputTokens;
                account.ReservedCost += command.MaxCost;
                account.Revision++;
            }
            _reservations.Add(reservation.Id, reservation);
            _attempts.Add(command.AttemptId, reservation);
            _operations[command.OperationId] = (fingerprint, reservation);
            return ValueTask.FromResult(reservation.ToContract());
        }
    }

    public ValueTask<DispatchPermit> MarkDispatchedAsync(DispatchBudget command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var reservation = FindReservation(command.ReservationId, command.AttemptId);
            if (reservation.State != AttemptState.Pending) throw Error(BudgetErrorCodes.DispatchInvalid, "Only a pending reservation can be dispatched.");
            if (command.OwnerEpoch < 1) throw Error(BudgetErrorCodes.RequestInvalid, "A owner epoch is required.");
            var chain = reservation.Chain;
            foreach (var account in chain)
            {
                if (account.Cancelled || account.CancellationRevision != command.ExpectedCancellationRevision)
                {
                    if (account.Cancelled) throw Error(BudgetErrorCodes.Cancelled, "Cancellation was recorded before the dispatch linearization point.");
                    throw Error(BudgetErrorCodes.Conflict, "The caller observed a stale cancellation revision.");
                }
            }
            reservation.State = AttemptState.Dispatched;
            reservation.OwnerEpoch = command.OwnerEpoch;
            foreach (var account in chain) account.Revision++;
            return ValueTask.FromResult(new DispatchPermit(reservation.Id, reservation.AttemptId, command.OwnerEpoch, chain[^1].CancellationRevision));
        }
    }

    public ValueTask<SettlementReceipt> SettleAsync(SettleBudget command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var reservation = FindReservation(command.ReservationId, command.AttemptId);
            if (reservation.State == AttemptState.Settled)
            {
                // Replaying the exact receipt is idempotent; a different receipt for a settled attempt is rejected.
                if (reservation.ReceiptFingerprint == Fingerprint(command)) return ValueTask.FromResult(reservation.LastReceipt!);
                throw Error(BudgetErrorCodes.SettlementConflict, "The attempt was already settled with a different receipt.");
            }
            if (reservation.State == AttemptState.Pending) throw Error(BudgetErrorCodes.SettlementInvalid, "An undispatched attempt cannot be settled.");
            if (command.OwnerEpoch < reservation.OwnerEpoch) throw Error(BudgetErrorCodes.OwnerStale, "A stale owner cannot settle this attempt.");
            if (!string.Equals(reservation.CostUnit, command.CostUnit, StringComparison.Ordinal))
                throw Error(BudgetErrorCodes.CurrencyMismatch, "The settlement cost unit differs from the reservation unit.");
            if (command.Usage is null) throw Error(BudgetErrorCodes.UsageInvalid, "Actual provider usage is required.");
            if (command.ActualCost < 0) throw Error(BudgetErrorCodes.RequestInvalid, "Actual cost cannot be negative.");
            try { ExecutionResourceScope.ValidateUsage(command.Usage); }
            catch (ExecutionResourceException) { throw Error(BudgetErrorCodes.UsageInvalid, "Actual provider usage is invalid."); }
            var fromUnknown = reservation.State == AttemptState.Unknown;
            var overrun = command.Usage.InputTokens > reservation.MaxInputTokens
                || command.Usage.OutputTokens > reservation.ReservedOutputTokens
                || command.ActualCost > reservation.ReservedCost;
            foreach (var account in reservation.Chain)
            {
                if (fromUnknown)
                {
                    account.UnknownOutputTokens = Math.Max(0, account.UnknownOutputTokens - reservation.ReservedOutputTokens);
                    account.UnknownCost = Math.Max(0, account.UnknownCost - reservation.ReservedCost);
                    if (account.UnknownOutputTokens == 0 && account.UnknownCost == 0 && account.FailureCode == BudgetErrorCodes.UsageUnknown)
                        account.FailureCode = null;
                }
                else
                {
                    account.ReservedOutputTokens = Math.Max(0, account.ReservedOutputTokens - reservation.ReservedOutputTokens);
                    account.ReservedCost = Math.Max(0, account.ReservedCost - reservation.ReservedCost);
                }
                account.ActiveAttempts = Math.Max(0, account.ActiveAttempts - 1);
                account.InputTokens += command.Usage.InputTokens;
                account.OutputTokens += command.Usage.OutputTokens;
                account.CacheHitTokens += command.Usage.CacheHitTokens;
                account.Cost += command.ActualCost;
                if (overrun) account.FailureCode = BudgetErrorCodes.ProviderOverrun;
                account.Revision++;
            }
            reservation.State = AttemptState.Settled;
            var receipt = new SettlementReceipt(reservation.Id, reservation.AttemptId, reservation.Chain[^1].Revision,
                command.Usage, command.ActualCost, command.CostUnit, overrun, fromUnknown);
            reservation.LastReceipt = receipt;
            reservation.ReceiptFingerprint = Fingerprint(command);
            return ValueTask.FromResult(receipt);
        }
    }

    public ValueTask<BudgetSnapshot> MarkUnknownAsync(MarkUnknownBudget command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var reservation = FindReservation(command.ReservationId, command.AttemptId);
            if (reservation.State == AttemptState.Pending) throw Error(BudgetErrorCodes.DispatchInvalid, "An undispatched attempt cannot become unknown.");
            if (reservation.State == AttemptState.Settled) throw Error(BudgetErrorCodes.UnknownStale, "A settled attempt is not unknown.");
            if (command.OwnerEpoch < reservation.OwnerEpoch) throw Error(BudgetErrorCodes.OwnerStale, "A stale owner cannot mark this attempt unknown.");
            if (reservation.State == AttemptState.Dispatched)
            {
                foreach (var account in reservation.Chain)
                {
                    account.ReservedOutputTokens = Math.Max(0, account.ReservedOutputTokens - reservation.ReservedOutputTokens);
                    account.ReservedCost = Math.Max(0, account.ReservedCost - reservation.ReservedCost);
                    account.UnknownOutputTokens += reservation.ReservedOutputTokens;
                    account.UnknownCost += reservation.ReservedCost;
                    account.FailureCode ??= BudgetErrorCodes.UsageUnknown;
                    account.Revision++;
                }
                reservation.State = AttemptState.Unknown;
            }
            return ValueTask.FromResult(reservation.Chain[^1].Snapshot());
        }
    }

    public ValueTask<BudgetSnapshot> ReleaseAsync(ReleaseBudget command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var reservation = FindReservation(command.ReservationId, command.AttemptId);
            if (reservation.State != AttemptState.Pending)
                throw Error(BudgetErrorCodes.DispatchInvalid, "Only an undispatched reservation can be released.");
            foreach (var account in reservation.Chain)
            {
                account.ReservedOutputTokens = Math.Max(0, account.ReservedOutputTokens - reservation.ReservedOutputTokens);
                account.ReservedCost = Math.Max(0, account.ReservedCost - reservation.ReservedCost);
                account.ActiveAttempts = Math.Max(0, account.ActiveAttempts - 1);
                account.Revision++;
            }
            reservation.State = AttemptState.Released;
            return ValueTask.FromResult(reservation.Chain[^1].Snapshot());
        }
    }

    public ValueTask<ImmutableArray<BudgetRecoveryCandidate>> ListUnsettledAsync(BudgetAccountId account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (account is null || !_accounts.TryGetValue(account.Value, out var found))
                throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            var subtree = Subtree(found).Select(a => a.Id).ToHashSet();
            var candidates = _reservations.Values
                .Where(r => r.State is AttemptState.Pending or AttemptState.Dispatched or AttemptState.Unknown
                            && subtree.Contains(r.Chain[^1].Id))
                .Select(r => new BudgetRecoveryCandidate(r.Id, r.AttemptId, r.OwnerEpoch, r.State switch
                {
                    AttemptState.Pending => BudgetAttemptPhase.Pending,
                    AttemptState.Dispatched => BudgetAttemptPhase.Dispatched,
                    _ => BudgetAttemptPhase.Unknown,
                }))
                .ToImmutableArray();
            return ValueTask.FromResult(candidates);
        }
    }

    public ValueTask<CancelBudgetReceipt> CancelAsync(CancelBudget command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (command.AccountId is null || !_accounts.TryGetValue(command.AccountId.Value, out var root))
                throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            if (string.IsNullOrWhiteSpace(command.ReasonCode) || command.ReasonCode.Length > 128)
                throw Error(BudgetErrorCodes.RequestInvalid, "A cancellation reason is required.");
            var alreadyCancelled = root.Cancelled;
            var subtree = Subtree(root).ToList();
            foreach (var account in subtree)
            {
                if (!account.Cancelled)
                {
                    account.Cancelled = true;
                    account.ReasonCode = command.ReasonCode;
                    account.Revision++;
                    account.CancellationRevision = account.Revision;
                }
            }
            var running = _reservations.Values.Count(r => r.State is AttemptState.Dispatched or AttemptState.Unknown
                && subtree.Contains(r.Chain[^1]));
            return ValueTask.FromResult(new CancelBudgetReceipt(root.Id, root.CancellationRevision, running, alreadyCancelled));
        }
    }

    public ValueTask<BudgetSnapshot> ReadAsync(BudgetAccountId account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (account is null || !_accounts.TryGetValue(account.Value, out var found))
                throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
            return ValueTask.FromResult(found.Snapshot());
        }
    }

    private Reservation FindReservation(string reservationId, string attemptId)
    {
        if (!_reservations.TryGetValue(reservationId ?? string.Empty, out var reservation) || reservation.AttemptId != attemptId)
            throw Error(BudgetErrorCodes.SettlementInvalid, "The reservation does not belong to this attempt.");
        return reservation;
    }

    /// <summary>Leaf-to-root walk. The Reservation.Dispatch path also blocks children of a cancelled ancestor.</summary>
    private List<Account> ResolveChain(BudgetAccountId accountId)
    {
        if (accountId is null || !_accounts.TryGetValue(accountId.Value, out var leaf))
            throw Error(BudgetErrorCodes.AccountMissing, "The budget account is missing.");
        var chain = new List<Account>();
        for (var current = leaf; current is not null; current = current.Parent)
        {
            chain.Add(current);
            if (chain.Count > 64) throw Error(BudgetErrorCodes.AccountInvalid, "The budget account depth is unbounded.");
        }
        chain.Reverse();
        return chain;
    }

    private IEnumerable<Account> Subtree(Account root)
    {
        yield return root;
        foreach (var child in _accounts.Values.Where(a => a.Parent == root).ToList())
            foreach (var nested in Subtree(child)) yield return nested;
    }

    private static void ValidateReserve(ReserveBudget command)
    {
        if (command is null) throw Error(BudgetErrorCodes.RequestInvalid, "A reserve command is required.");
        if (!Identifier(command.OperationId) || !Identifier(command.AttemptId))
            throw Error(BudgetErrorCodes.RequestInvalid, "Operation and attempt ids are required.");
        if (command.AccountId is null || !command.AccountId.IsValid
            || command.Identity is null || !command.Identity.IsValid)
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

    private static string Fingerprint(ReserveBudget command) => string.Join('|',
        command.OperationId, command.AttemptId, command.AccountId.Value, string.Join(',', command.AccountPath.Select(a => a.Value)),
        command.Identity.RootKind, command.Identity.RootId, command.Identity.WorkspaceId, command.Identity.AgentId, command.Identity.SessionId,
        command.ModelKey, command.ModelRevision, command.PriceVersion, command.MaxInputTokens, command.MaxOutputTokens,
        command.MaxCost.ToString("G29"), command.CostUnit);

    private static string Fingerprint(SettleBudget command) => string.Join('|',
        command.AttemptId, command.Usage.InputTokens, command.Usage.OutputTokens, command.Usage.CacheHitTokens,
        command.ActualCost.ToString("G29"), command.CostUnit, command.ProviderReceipt);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ExecutionResourceException Error(string code, string message) => new(code, message);

    private enum AttemptState { Pending, Dispatched, Unknown, Settled, Released }

    private sealed class Account(BudgetAccountId id, Account? parent, BudgetLimits limits, BudgetIdentity identity, string costUnit)
    {
        public BudgetAccountId Id { get; } = id;
        public Account? Parent { get; } = parent;
        public BudgetLimits Limits { get; } = limits;
        public BudgetIdentity Identity { get; } = identity;
        public string CostUnit { get; } = costUnit;
        public long InputTokens;
        public long OutputTokens;
        public long CacheHitTokens;
        public decimal Cost;
        public long ReservedOutputTokens;
        public decimal ReservedCost;
        public long UnknownOutputTokens;
        public decimal UnknownCost;
        public int Calls;
        public int ActiveAttempts;
        public long Revision;
        public bool Cancelled;
        public long CancellationRevision;
        public string? ReasonCode;
        public string? FailureCode;
        public BudgetSnapshot Snapshot() => new(Id, Revision, Limits, InputTokens, OutputTokens, CacheHitTokens, Cost, CostUnit,
            ReservedOutputTokens, ReservedCost, UnknownOutputTokens, UnknownCost, Calls, ActiveAttempts, Cancelled,
            CancellationRevision, ReasonCode ?? FailureCode);
    }

    private sealed class Reservation(string id, ReserveBudget command, List<Account> chain)
    {
        public string Id { get; } = id;
        public string AttemptId { get; } = command.AttemptId;
        public List<Account> Chain { get; } = chain;
        public string CostUnit { get; } = command.CostUnit;
        public long MaxInputTokens { get; } = command.MaxInputTokens;
        public long ReservedOutputTokens { get; } = command.MaxOutputTokens;
        public decimal ReservedCost { get; } = command.MaxCost;
        public AttemptState State;
        public long OwnerEpoch;
        public SettlementReceipt? LastReceipt;
        public string? ReceiptFingerprint;
        public BudgetReservation ToContract() => new(Id, Chain[^1].Id, Chain[0].Id, AttemptId, Chain[^1].Revision,
            Chain[^1].CancellationRevision, ReservedOutputTokens, ReservedCost, CostUnit);
    }
}
