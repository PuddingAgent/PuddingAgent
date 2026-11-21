using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("external_agent_delegation_results")]
public sealed class ExternalAgentDelegationResultEntity
{
    [Key, Column("message_id")]
    public required string MessageId { get; init; }

    [Column("state")]
    public required string State { get; init; }

    [Column("turn_id")]
    public required string TurnId { get; init; }

    [Column("result_text")]
    public string? Text { get; init; }

    [Column("reason_code")]
    public string? Reason { get; init; }

    [Column("reported_at")]
    public long ReportedAt { get; init; }
}
