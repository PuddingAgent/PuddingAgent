using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>PA-01 S5: durable budget account. One row per host-owned account id; limits never change in place.</summary>
[Table("execution_budget_accounts")]
public sealed class ExecutionBudgetAccountEntity
{
    [Key] [Column("account_id")] public string AccountId { get; set; } = "";
    [Column("parent_account_id")] public string? ParentAccountId { get; set; }
    [Column("root_kind")] public int RootKind { get; set; }
    [Column("root_id")] public string RootId { get; set; } = "";
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("agent_id")] public string AgentId { get; set; } = "";
    [Column("session_id")] public string SessionId { get; set; } = "";
    [Column("cost_unit")] public string CostUnit { get; set; } = "";
    [Column("max_output_tokens")] public long? MaxOutputTokens { get; set; }
    [Column("max_cost")] public string? MaxCost { get; set; }
    [Column("max_calls")] public int? MaxCalls { get; set; }
    [Column("max_concurrent_attempts")] public int MaxConcurrentAttempts { get; set; } = 2;
    [Column("max_input_tokens_per_request")] public long? MaxInputTokensPerRequest { get; set; }
    [Column("enforcement_mode")] public int EnforcementMode { get; set; }
    [Column("input_tokens")] public long InputTokens { get; set; }
    [Column("output_tokens")] public long OutputTokens { get; set; }
    [Column("cache_hit_tokens")] public long CacheHitTokens { get; set; }
    [Column("cost")] public string Cost { get; set; } = "0";
    [Column("reserved_output_tokens")] public long ReservedOutputTokens { get; set; }
    [Column("reserved_cost")] public string ReservedCost { get; set; } = "0";
    [Column("unknown_output_tokens")] public long UnknownOutputTokens { get; set; }
    [Column("unknown_cost")] public string UnknownCost { get; set; } = "0";
    [Column("calls")] public int Calls { get; set; }
    [Column("active_attempts")] public int ActiveAttempts { get; set; }
    [Column("revision")] public long Revision { get; set; }
    [Column("cancelled")] public bool Cancelled { get; set; }
    [Column("cancellation_revision")] public long CancellationRevision { get; set; }
    [Column("reason_code")] public string? ReasonCode { get; set; }
    [Column("failure_code")] public string? FailureCode { get; set; }
}

/// <summary>PA-01 S5: durable attempt reservation, dispatch state, unknown retention and settlement receipt.</summary>
[Table("execution_budget_reservations")]
public sealed class ExecutionBudgetReservationEntity
{
    [Key] [Column("reservation_id")] public string ReservationId { get; set; } = "";
    [Column("account_id")] public string AccountId { get; set; } = "";
    [Column("root_account_id")] public string RootAccountId { get; set; } = "";
    [Column("attempt_id")] public string AttemptId { get; set; } = "";
    [Column("operation_id")] public string OperationId { get; set; } = "";
    [Column("fingerprint")] public string Fingerprint { get; set; } = "";
    [Column("chain")] public string Chain { get; set; } = "";
    [Column("cost_unit")] public string CostUnit { get; set; } = "";
    [Column("max_input_tokens")] public long MaxInputTokens { get; set; }
    [Column("reserved_output_tokens")] public long ReservedOutputTokens { get; set; }
    [Column("reserved_cost")] public string ReservedCost { get; set; } = "0";
    [Column("state")] public int State { get; set; }
    [Column("owner_epoch")] public long OwnerEpoch { get; set; }
    [Column("receipt_input_tokens")] public long? ReceiptInputTokens { get; set; }
    [Column("receipt_output_tokens")] public long? ReceiptOutputTokens { get; set; }
    [Column("receipt_cache_hit_tokens")] public long? ReceiptCacheHitTokens { get; set; }
    [Column("receipt_cost")] public string? ReceiptCost { get; set; }
    [Column("receipt_cost_unit")] public string? ReceiptCostUnit { get; set; }
    [Column("receipt_overrun")] public bool? ReceiptOverrun { get; set; }
    [Column("receipt_from_unknown")] public bool? ReceiptFromUnknown { get; set; }
    [Column("receipt_fingerprint")] public string? ReceiptFingerprint { get; set; }
    [Column("receipt_revision")] public long? ReceiptRevision { get; set; }
}
