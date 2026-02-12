using System.Globalization;
using System.Text.Json.Serialization;

namespace PuddingCode.Models;

/// <summary>Canonical message context delivered to an agent as an LLM-readable envelope.</summary>
public sealed record AgentContextEnvelope
{
    /// <summary>Constant schema marker identifying this payload as a Pudding agent context envelope.</summary>
    [JsonPropertyName("schema")]
    public string Schema => "pudding-message";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("message_type")]
    public required string MessageType { get; init; }

    [JsonPropertyName("content_type")]
    public required string ContentType { get; init; }

    [JsonPropertyName("created_at")]
    public required long CreatedAt { get; init; }

    /// <summary>ISO-8601 round-trip representation of <see cref="CreatedAt"/>, derived at serialization time.</summary>
    [JsonPropertyName("created_at_iso")]
    public string CreatedAtIso =>
        DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime().ToString("O", CultureInfo.InvariantCulture);

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("room_id")]
    public string? RoomId { get; init; }

    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("reply_to_message_id")]
    public string? ReplyToMessageId { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("causation_id")]
    public string? CausationId { get; init; }

    [JsonPropertyName("from")]
    public required AgentContextEndpoint From { get; init; }

    [JsonPropertyName("to")]
    public required IReadOnlyList<AgentContextEndpoint> To { get; init; }

    [JsonPropertyName("constraints")]
    public required IReadOnlyList<string> Constraints { get; init; }

    [JsonPropertyName("context")]
    public required AgentContextPayload Context { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record AgentContextEndpoint
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public AgentContextEndpoint(string kind, string id, string? displayName)
    {
        Kind = kind;
        Id = id;
        DisplayName = displayName;
    }
}

public sealed record AgentContextPayload
{
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public AgentContextPayload(string format, string text)
    {
        Format = format;
        Text = text;
    }
}
