using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>PA-07A S5: durable plugin job protocol. State belongs to the job protocol, never to Review/Goal.</summary>
[Table("plugin_jobs")]
public sealed class PluginJobEntity
{
    [Key] [Column("job_id")] public string JobId { get; set; } = "";
    [Column("plugin_id")] public string PluginId { get; set; } = "";
    [Column("plugin_generation")] public long PluginGeneration { get; set; }
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("owner_work_id")] public string OwnerWorkId { get; set; } = "";
    [Column("root_budget_id")] public string RootBudgetId { get; set; } = "";
    [Column("kind")] public string Kind { get; set; } = "";
    [Column("operation_id")] public string OperationId { get; set; } = "";
    [Column("input_ref")] public string InputRef { get; set; } = "";
    [Column("input_hash")] public string InputHash { get; set; } = "";
    [Column("timeout_seconds")] public int TimeoutSeconds { get; set; }
    [Column("required_grants")] public string RequiredGrants { get; set; } = "";
    [Column("deadline")] public DateTimeOffset Deadline { get; set; }
    [Column("accepted_at")] public DateTimeOffset AcceptedAt { get; set; }
    [Column("accepted_sequence")] public long AcceptedSequence { get; set; }
    [Column("state")] public int State { get; set; }
    [Column("reason_code")] public string? ReasonCode { get; set; }
    [Column("result_ref")] public string? ResultRef { get; set; }
    [Column("result_hash")] public string? ResultHash { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [Column("attempt")] public int Attempt { get; set; }
}

/// <summary>PA-07A S5: submit idempotency by (workspace, operation); different content under the same key conflicts.</summary>
[Table("plugin_job_operations")]
public sealed class PluginJobOperationEntity
{
    [Key] [Column("operation_key")] public string OperationKey { get; set; } = "";
    [Column("job_id")] public string JobId { get; set; } = "";
    [Column("fingerprint")] public string Fingerprint { get; set; } = "";
}
