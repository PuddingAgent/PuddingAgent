using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Models;

namespace PuddingCode.Services;

/// <summary>Renders canonical agent context envelopes into deterministic LLM-readable JSON text.</summary>
public static class AgentContextEnvelopeRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serializes the envelope to canonical pudding-message JSON.</summary>
    public static string RenderForAgent(AgentContextEnvelope envelope)
        => JsonSerializer.Serialize(envelope, JsonOptions);

    /// <summary>Attempts to deserialize a JSON payload back into an envelope. Returns null on failure.</summary>
    public static AgentContextEnvelope? TryParse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentContextEnvelope>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Dictionary<string, string> FlattenMetadata(AgentContextEnvelope envelope)
    {
        var result = new Dictionary<string, string>(envelope.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["pudding_message_version"] = envelope.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["message_type"] = envelope.MessageType,
            ["content_type"] = envelope.ContentType,
            ["from_kind"] = envelope.From.Kind,
            ["from_id"] = envelope.From.Id,
        };

        if (!string.IsNullOrWhiteSpace(envelope.ConversationId))
            result["conversation_id"] = envelope.ConversationId!;
        if (!string.IsNullOrWhiteSpace(envelope.CorrelationId))
            result["correlation_id"] = envelope.CorrelationId!;
        if (!string.IsNullOrWhiteSpace(envelope.CausationId))
            result["causation_id"] = envelope.CausationId!;

        return result;
    }
}
