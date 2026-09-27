using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingApproval;

/// <summary>Exact operation shown to the human and retained with its permit. Runtime supplies resolved resources in arguments.</summary>
public sealed record ApprovalOperation(string ToolId, string ArgumentsJson, string ToolDefinitionJson, string ExecutionRoot)
{
    public const int MaxJsonCharacters = 1_048_576;

    /// <summary>Versioned digest, independent of JSON property order/whitespace. Array order and numeric spelling are significant.</summary>
    public string Fingerprint()
    {
        if (string.IsNullOrWhiteSpace(ToolId) || ToolId.Length > 512
            || string.IsNullOrWhiteSpace(ExecutionRoot) || !Path.IsPathFullyQualified(ExecutionRoot))
            throw new ArgumentException("Approval requires a tool ID and resolved absolute execution root.");
        // Do not infer filesystem aliases, resolve symlinks, fold case, or expand environment variables here.
        // Runtime must supply the same resolved execution-root identity at request and commit.
        var arguments = CanonicalObject(ArgumentsJson);
        var definition = CanonicalObject(ToolDefinitionJson);
        var payload = JsonSerializer.Serialize(new[] { "pudding-operation-v1", ToolId, ExecutionRoot, arguments, definition });
        return "sha256:v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string CanonicalObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters)
            throw new ArgumentException("Approval arguments and tool definition must be bounded JSON objects.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Approval arguments and tool definition must be JSON objects.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!properties.TryAdd(property.Name, property.Value))
                        throw new ArgumentException("Duplicate JSON keys cannot be bound to a single approved operation.");
                writer.WriteStartObject();
                foreach (var property in properties) { writer.WritePropertyName(property.Key); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
}
