using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessAgent.Core.Mcp;

/// <summary>JSON-RPC 2.0 message types.</summary>
public static class JsonRpc
{
    public const string Version = "2.0";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A JSON-RPC request.</summary>
    public sealed record Request
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; init; } = Version;

        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("method")]
        public required string Method { get; init; }

        [JsonPropertyName("params")]
        public JsonElement? Params { get; init; }
    }

    /// <summary>A JSON-RPC response (success).</summary>
    public sealed record Response
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; init; } = Version;

        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("result")]
        public JsonElement Result { get; init; }
    }

    /// <summary>A JSON-RPC error response.</summary>
    public sealed record ErrorResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; init; } = Version;

        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("error")]
        public required ErrorDetail Error { get; init; }
    }

    /// <summary>JSON-RPC error detail.</summary>
    public sealed record ErrorDetail
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("data")]
        public JsonElement? Data { get; init; }
    }

    /// <summary>A JSON-RPC notification (no id, no response expected).</summary>
    public sealed record Notification
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; init; } = Version;

        [JsonPropertyName("method")]
        public required string Method { get; init; }

        [JsonPropertyName("params")]
        public JsonElement? Params { get; init; }
    }

    /// <summary>Serialize a request to JSON.</summary>
    public static string SerializeRequest(Request request) =>
        JsonSerializer.Serialize(request, Options);

    /// <summary>Deserialize a JSON string to the appropriate type.</summary>
    public static (Request? Request, Response? Response, ErrorResponse? Error, Notification? Notification)
        Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var hasId = root.TryGetProperty("id", out _);
        var hasMethod = root.TryGetProperty("method", out _);
        var hasError = root.TryGetProperty("error", out _);

        if (hasError)
            return (null, null,
                JsonSerializer.Deserialize<ErrorResponse>(json, Options), null);

        if (hasMethod && !hasId)
            return (null, null, null,
                JsonSerializer.Deserialize<Notification>(json, Options));

        if (hasMethod)
            return (JsonSerializer.Deserialize<Request>(json, Options),
                null, null, null);

        return (null,
            JsonSerializer.Deserialize<Response>(json, Options), null, null);
    }
}
