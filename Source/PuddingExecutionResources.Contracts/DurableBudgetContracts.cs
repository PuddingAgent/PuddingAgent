using System.Collections.Immutable;

namespace PuddingExecutionResources.Contracts;

// PA-01 01A 冻结端口：跨轮持久预算账户的异步事务面。
// 不变量：根标识、账户路径与 owner epoch 由宿主绑定；调用方字符串不能扩大范围；
// 一次 Reserve 事务检查全部祖先；取消与派发通过 revision 线性化；未知效果不得当作零。

/// <summary>Opaque host-owned account identifier. Callers never bind or widen it.</summary>
public sealed record BudgetAccountId(string Value)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    public override string ToString() => Value;
}

public enum BudgetAccountKind { GoalRun, Work, Task }

/// <summary>Server-bound root identity. The host maps it to an existing GoalRunId, WorkId or task id.</summary>
public sealed record BudgetIdentity(BudgetAccountKind RootKind, string RootId, string WorkspaceId, string AgentId, string SessionId)
{
    public bool IsValid => RootKind is BudgetAccountKind.GoalRun or BudgetAccountKind.Work or BudgetAccountKind.Task
        && !string.IsNullOrWhiteSpace(RootId) && RootId.Length <= 128
        && !string.IsNullOrWhiteSpace(WorkspaceId) && WorkspaceId.Length <= 128
        && !string.IsNullOrWhiteSpace(AgentId) && AgentId.Length <= 128
        && !string.IsNullOrWhiteSpace(SessionId) && SessionId.Length <= 128;
}

/// <summary>Null means no limit on that dimension; zero means exhausted. Input is per-request capacity, never cumulative usage.</summary>
public sealed record BudgetLimits(long? MaxOutputTokens = null, decimal? MaxCost = null, int? MaxCalls = null,
    int MaxConcurrentAttempts = 2, long? MaxInputTokensPerRequest = null);

public sealed record ReserveBudget(
    string OperationId,
    string AttemptId,
    BudgetAccountId AccountId,
    ImmutableArray<BudgetAccountId> AccountPath,
    BudgetIdentity Identity,
    string ModelKey,
    string ModelRevision,
    string PriceVersion,
    long MaxInputTokens,
    long MaxOutputTokens,
    decimal MaxCost,
    string CostUnit,
    DateTimeOffset Deadline);

public sealed record BudgetReservation(string ReservationId, BudgetAccountId AccountId, BudgetAccountId RootAccountId,
    string AttemptId, long Revision, long CancellationRevision, long ReservedOutputTokens, decimal ReservedCost, string CostUnit);

public sealed record DispatchBudget(string ReservationId, string AttemptId, long OwnerEpoch, long ExpectedCancellationRevision);

public sealed record DispatchPermit(string ReservationId, string AttemptId, long OwnerEpoch, long CancellationRevision);

public sealed record SettleBudget(string ReservationId, string AttemptId, long OwnerEpoch, ModelUsage Usage,
    decimal ActualCost, string CostUnit, string ProviderReceipt, DateTimeOffset OccurredAt);

public sealed record SettlementReceipt(string ReservationId, string AttemptId, long Revision, ModelUsage Usage,
    decimal Cost, string CostUnit, bool Overrun, bool FromUnknown);

public sealed record MarkUnknownBudget(string ReservationId, string AttemptId, long OwnerEpoch, string ReasonCode);

/// <summary>Refunds a reservation that was never dispatched. A dispatched attempt cannot be released; settle or mark unknown instead.</summary>
public sealed record ReleaseBudget(string ReservationId, string AttemptId);

public sealed record CancelBudget(BudgetAccountId AccountId, string ReasonCode, long OwnerEpoch);

/// <summary>Records the cancellation, never claims the provider stopped. Running attempts remain visible.</summary>
public sealed record CancelBudgetReceipt(BudgetAccountId AccountId, long CancellationRevision, int RunningAttempts, bool AlreadyCancelled);

public sealed record BudgetSnapshot(BudgetAccountId AccountId, long Revision, BudgetLimits Limits,
    long InputTokens, long OutputTokens, long CacheHitTokens, decimal Cost, string CostUnit,
    long ReservedOutputTokens, decimal ReservedCost, long UnknownOutputTokens, decimal UnknownCost,
    int Calls, int ActiveAttempts, bool Cancelled, long CancellationRevision, string? ReasonCode)
{
    /// <summary>Unknown amounts stay unknown; they are never reported as zero or as spendable balance. Null means unbounded.</summary>
    public long? RemainingOutputTokens => Limits.MaxOutputTokens is { } limit
        ? Math.Max(0, limit - OutputTokens - UnknownOutputTokens - ReservedOutputTokens)
        : null;
    public decimal? RemainingCost => Limits.MaxCost is { } limit
        ? Math.Max(0, limit - Cost - UnknownCost - ReservedCost)
        : null;
}

/// <summary>One authoritative ledger per host root. The in-memory implementation is for independent tests; the product wires a persistent one.</summary>
public interface IExecutionBudgetLedger
{
    ValueTask<BudgetReservation> ReserveAsync(ReserveBudget command, CancellationToken ct = default);
    ValueTask<DispatchPermit> MarkDispatchedAsync(DispatchBudget command, CancellationToken ct = default);
    ValueTask<SettlementReceipt> SettleAsync(SettleBudget command, CancellationToken ct = default);
    ValueTask<BudgetSnapshot> MarkUnknownAsync(MarkUnknownBudget command, CancellationToken ct = default);
    ValueTask<BudgetSnapshot> ReleaseAsync(ReleaseBudget command, CancellationToken ct = default);
    ValueTask<CancelBudgetReceipt> CancelAsync(CancelBudget command, CancellationToken ct = default);
    ValueTask<BudgetSnapshot> ReadAsync(BudgetAccountId account, CancellationToken ct = default);
}

public enum BudgetAttemptPhase { Pending, Dispatched, Unknown }

/// <summary>An unsettled attempt found in persistent storage during recovery. Unknown stays unknown; it is never silently refunded.</summary>
public sealed record BudgetRecoveryCandidate(string ReservationId, string AttemptId, long OwnerEpoch, BudgetAttemptPhase Phase);

/// <summary>Host persistence seam used only by the restart reconciler; independent tests implement it on the in-memory ledger.</summary>
public interface IExecutionBudgetRecoveryLedger : IExecutionBudgetLedger
{
    ValueTask<ImmutableArray<BudgetRecoveryCandidate>> ListUnsettledAsync(BudgetAccountId account, CancellationToken ct = default);
}

public static class BudgetErrorCodes
{
    public const string RequestInvalid = "budget.request_invalid";
    public const string AccountMissing = "budget.account_missing";
    public const string AccountExists = "budget.account_exists";
    public const string AccountInvalid = "budget.account_invalid";
    public const string PathInvalid = "budget.path_invalid";
    public const string Conflict = "budget.conflict";
    public const string Exhausted = "budget.exhausted";
    public const string Concurrency = "budget.concurrency";
    public const string InputCapacity = "budget.input_capacity";
    public const string Cancelled = "budget.cancelled";
    public const string OwnerStale = "budget.owner_stale";
    public const string DispatchInvalid = "budget.dispatch_invalid";
    public const string SettlementInvalid = "budget.settlement_invalid";
    public const string SettlementConflict = "budget.settlement_conflict";
    public const string CurrencyMismatch = "budget.currency_mismatch";
    public const string ProviderOverrun = "budget.provider_overrun";
    public const string UsageUnknown = "budget.usage_unknown";
    public const string UsageInvalid = "budget.usage_invalid";
    public const string UnknownStale = "budget.unknown_stale";
}

public static class BudgetAccountPaths
{
    /// <summary>Root-to-leaf path. The ledger verifies it against its own parent chain; a supplied path can only be exact, never wider.</summary>
    public static ImmutableArray<BudgetAccountId> RootToLeaf(BudgetAccountId account, ImmutableArray<BudgetAccountId> ancestors)
        => ancestors.IsDefault ? [account] : [.. ancestors, account];
}
