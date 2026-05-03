using System.Text.Json;

namespace PuddingCode.Platform;

/// <summary>
/// Raw Server-Sent Event frame used to proxy streaming chat events across
/// Platform → Controller → Runtime without buffering the LLM response.
/// Data is already serialized JSON so intermediate layers can pass it through.
/// </summary>
public sealed record ServerSentEventFrame(string Event, string Data)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Create a frame from a typed payload using web/camelCase JSON options.</summary>
    public static ServerSentEventFrame Json(string eventName, object payload) =>
        new(eventName, JsonSerializer.Serialize(payload, JsonOptions));
}
