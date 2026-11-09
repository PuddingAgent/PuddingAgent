using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>PA-07A S5: a durable plugin subscription grant and its checkpoint cursor.</summary>
[Table("event_subscriptions")]
public sealed class EventSubscriptionEntity
{
    [Key] [Column("subscription_id")] public string SubscriptionId { get; set; } = "";
    [Column("plugin_id")] public string PluginId { get; set; } = "";
    [Column("generation")] public long Generation { get; set; }
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("kinds")] public string Kinds { get; set; } = "";
    [Column("cursor_sequence")] public long CursorSequence { get; set; }
    [Column("scanned_through_sequence")] public long ScannedThroughSequence { get; set; }
    [Column("revision")] public long Revision { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [Column("cancelled")] public bool Cancelled { get; set; }
}

/// <summary>PA-07A S5: an at-least-once delivery leased for one subscription.</summary>
[Table("event_deliveries")]
public sealed class EventDeliveryEntity
{
    [Key] [Column("delivery_id")] public string DeliveryId { get; set; } = "";
    [Column("subscription_id")] public string SubscriptionId { get; set; } = "";
    [Column("event_id")] public string EventId { get; set; } = "";
    [Column("event_sequence")] public long EventSequence { get; set; }
    [Column("envelope_json")] public string EnvelopeJson { get; set; } = "";
    [Column("attempt")] public int Attempt { get; set; }
    [Column("claimed_at")] public DateTimeOffset ClaimedAt { get; set; }
    [Column("failure_code")] public string? FailureCode { get; set; }
    [Column("dead_lettered")] public bool DeadLettered { get; set; }
    [Column("lease_owner_id")] public string? LeaseOwnerId { get; set; }
    [Column("lease_until_unix_ms")] public long? LeaseUntilUnixMs { get; set; }
}
