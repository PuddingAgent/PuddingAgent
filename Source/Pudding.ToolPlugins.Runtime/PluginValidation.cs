using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Runtime;

public static class PluginValidation
{
    public static void Manifest(PluginManifest m)
    {
        Require(m.Schema == "pudding-tool-plugin/v2" && m.SdkApi == "1.0" && m.CoreApi is "1.0" or "1.1" or "1.2" or "1.3",
            "plugin.api_incompatible", "Supported schema: pudding-tool-plugin/v2; SDK 1.0; Core API 1.0/1.1/1.2/1.3.");
        Require(m.Runtime is { Framework: "net10.0", Rid: "win-x64" }, "plugin.platform_incompatible", "Only net10.0 / win-x64 is supported.");
        Require(Regex.IsMatch(m.Id ?? "", "^[a-z0-9][a-z0-9.-]{1,63}$") && !m.Id!.Contains("..")
            && Regex.IsMatch(m.Version ?? "", "^[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}$")
            && !string.IsNullOrWhiteSpace(m.Name) && m.Name.Length <= 128, "plugin.manifest_invalid", "Invalid package identity.");
        Require(m.Entry is not null && !string.IsNullOrWhiteSpace(m.Entry.Type) && m.Entry.Type.Length <= 256,
            "plugin.entry_invalid", "Entry type is required.");
        RelativePath(m.Entry!.Assembly);
        Require(m.Entry.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), "plugin.entry_invalid", "Entry must be a DLL.");
        Require(m.Limits is { ExecutionTimeoutSeconds: >= 1 and <= 60, MaxConcurrentCalls: 1 },
            "plugin.manifest_invalid", "Timeout must be 1..60 seconds; one concurrent call per instance.");
        Require(m.RequestedCapabilities is not null && m.RequestedCapabilities.All(c => c is "workspace.read" or "assets.read" or "state.read" or "state.write"
                || (m.CoreApi is "1.1" or "1.2" or "1.3") && (c is "agent.configuration.read" or "agent.configuration.update" or "agent.configuration.changes")
                || (m.CoreApi is "1.2" or "1.3") && (c is "operators.list" or "operators.skill-value.score")
                || m.CoreApi == "1.3" && (c is "operators.model.list" or "operators.text.classify" or "operators.delivery.review")),
            "plugin.capability_denied", "Unsupported capability.");
        Require(m.Tools is not null && m.Hooks is not null && m.Subscriptions is not null && m.Assets is not null
            && m.Tools.Length + m.Hooks.Length + m.Subscriptions.Length is > 0 and <= 32 && m.Assets.Length <= 128,
            "plugin.manifest_invalid", "Expected 1..32 contributions and at most 128 assets.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in m.Tools!)
        {
            Require(t is not null && ValidId(t.Id) && ids.Add(t.Id) && !string.IsNullOrWhiteSpace(t.Name)
                && t.Name.Length <= 128 && !string.IsNullOrWhiteSpace(t.Description) && t.Description.Length <= 4096,
                "plugin.manifest_invalid", "Invalid or repeated tool definition.");
            Require(t!.Effects is not null && t.Effects.Length > 0 && t.Effects.All(e => e is "none" or "filesystem.read" or "filesystem.write" or "network" or "process"
                    || (m.CoreApi is "1.1" or "1.2" or "1.3") && e == "agent.configuration.write")
                && (t.Effects.Length == 1 || !t.Effects.Contains("none")), "plugin.manifest_invalid", "Unknown or contradictory effects.");
            Require(t.Category is "General" or "Query" or "FileSystem" or "Network" or "Execute" or "Messaging",
                "plugin.manifest_invalid", "Unsupported tool category.");
            Schema(t.InputSchema, 0); Schema(t.OutputSchema, 0);
            Require(t.InputSchema.GetProperty("type").GetString() == "object", "plugin.manifest_invalid", "Tool input must be an object.");
        }
        foreach (var h in m.Hooks!)
            Require(h is not null && ValidId(h.Id) && ids.Add(h.Id) && h.Event == "tool.result.advice"
                && h.Order is >= -1000 and <= 1000, "plugin.manifest_invalid", "Unsupported or repeated hook contribution.");
        foreach (var s in m.Subscriptions!)
            Require(s is not null && ValidId(s.Id) && ids.Add(s.Id) && PluginEvents.All.Contains(s.Event, StringComparer.Ordinal),
                "plugin.manifest_invalid", "Unsupported or repeated workspace event subscription.");
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in m.Assets!)
        {
            Require(a is not null, "plugin.manifest_invalid", "Asset must be an object.");
            RelativePath(a!.Path);
            Require(assets.Add(a.Path) && a.Kind == "text", "plugin.manifest_invalid", "Assets must be unique text paths.");
        }
    }

    public static bool ValidId(string value) => Regex.IsMatch(value ?? "", "^[a-zA-Z][a-zA-Z0-9_]{0,31}$");
    public static string FunctionName(string pluginId, string toolId, string generation)
        => $"px_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pluginId)))[..8].ToLowerInvariant()}_{generation[..8]}_{toolId}";

    public static string RelativePath(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && path.Length <= 200 && !Path.IsPathRooted(path)
            && !path.Contains('\\') && !path.StartsWith('/'), "plugin.path_rejected", "Expected canonical relative path using '/'.");
        foreach (var segment in path.Split('/'))
            Require(segment.Length > 0 && segment is not "." and not ".." && !segment.EndsWith('.') && !segment.EndsWith(' ')
                && !segment.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                && !Regex.IsMatch(segment, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase),
                "plugin.path_rejected", "Unsafe path segment.");
        return path;
    }
    public static string Inside(string root, string relative)
    {
        RelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        Require(path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "plugin.path_rejected", "Path escapes root.");
        NoLinks(path); return path;
    }
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "plugin.path_rejected", "Reparse points are not supported.");
    }
    public static void Require(bool condition, string code, string message)
    { if (!condition) throw new PluginException(code, "validate", message); }

    private static readonly HashSet<string> Keywords = ["type", "description", "properties", "required", "additionalProperties", "items", "enum", "maxLength", "minLength", "minimum", "maximum", "minItems", "maxItems"];
    public static void Schema(JsonElement schema, int depth)
    {
        Require(depth <= 8 && schema.ValueKind == JsonValueKind.Object, "plugin.schema_invalid", "Schema must be a bounded object.");
        var seen = new HashSet<string>();
        foreach (var p in schema.EnumerateObject()) Require(Keywords.Contains(p.Name) && seen.Add(p.Name), "plugin.schema_invalid", "Unsupported or repeated schema keyword.");
        Require(schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            && t.GetString() is "object" or "array" or "string" or "integer" or "number" or "boolean", "plugin.schema_invalid", "Unsupported schema type.");
        if (schema.TryGetProperty("properties", out var props))
        {
            Require(props.ValueKind == JsonValueKind.Object && props.EnumerateObject().Count() <= 64, "plugin.schema_invalid", "Too many properties.");
            var names = new HashSet<string>();
            foreach (var p in props.EnumerateObject()) { Require(names.Add(p.Name), "plugin.schema_invalid", "Duplicate property."); Schema(p.Value, depth + 1); }
        }
        if (schema.TryGetProperty("required", out var required))
            Require(required.ValueKind == JsonValueKind.Array && required.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && props.ValueKind == JsonValueKind.Object && props.TryGetProperty(v.GetString()!, out _)), "plugin.schema_invalid", "Invalid required properties.");
        if (schema.TryGetProperty("additionalProperties", out var additional))
            Require(additional.ValueKind is JsonValueKind.True or JsonValueKind.False, "plugin.schema_invalid", "additionalProperties must be boolean.");
        if (schema.TryGetProperty("items", out var items)) Schema(items, depth + 1);
        Require(t.GetString() != "array" || schema.TryGetProperty("items", out _), "plugin.schema_invalid", "Array items schema is required.");
        if (schema.TryGetProperty("enum", out var values)) Require(values.ValueKind == JsonValueKind.Array && values.GetArrayLength() is > 0 and <= 100, "plugin.schema_invalid", "Invalid enum.");
        foreach (var name in new[] { "maxLength", "minLength", "minItems", "maxItems" })
            if (schema.TryGetProperty(name, out var number)) Require(number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var n) && n >= 0, "plugin.schema_invalid", "Invalid size bound.");
        foreach (var name in new[] { "minimum", "maximum" })
            if (schema.TryGetProperty(name, out var number)) Require(number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out _), "plugin.schema_invalid", "Invalid numeric bound.");
    }
    public static void Value(JsonElement schema, JsonElement value, int depth = 0)
    {
        Require(depth <= 8, "plugin.arguments_invalid", "Payload nesting limit exceeded.");
        var type = schema.GetProperty("type").GetString();
        var matches = type switch
        { "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
          "string" => value.ValueKind == JsonValueKind.String, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
          "integer" => value.TryGetInt64Safe(), "number" => value.ValueKind == JsonValueKind.Number, _ => false };
        Require(matches, "plugin.arguments_invalid", "Value does not match declared type.");
        if (schema.TryGetProperty("enum", out var values)) Require(values.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)), "plugin.arguments_invalid", "Value not in enum.");
        if (type == "object")
        {
            schema.TryGetProperty("properties", out var props);
            var seen = new HashSet<string>();
            foreach (var p in value.EnumerateObject())
            {
                Require(seen.Add(p.Name), "plugin.arguments_invalid", "Duplicate argument.");
                if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(p.Name, out var s)) Value(s, p.Value, depth + 1);
                else Require(!schema.TryGetProperty("additionalProperties", out var allowed) || allowed.GetBoolean(), "plugin.arguments_invalid", "Unexpected argument.");
            }
            if (schema.TryGetProperty("required", out var req)) foreach (var name in req.EnumerateArray()) Require(value.TryGetProperty(name.GetString()!, out _), "plugin.arguments_invalid", "Required argument missing.");
        }
        if (type == "array") foreach (var item in value.EnumerateArray()) Value(schema.GetProperty("items"), item, depth + 1);
        if (type is "array" or "string")
        {
            var n = type == "array" ? value.GetArrayLength() : value.GetString()!.Length;
            var suffix = type == "array" ? "Items" : "Length";
            if (schema.TryGetProperty("min" + suffix, out var min)) Require(n >= min.GetInt32(), "plugin.arguments_invalid", "Value too short.");
            if (schema.TryGetProperty("max" + suffix, out var max)) Require(n <= max.GetInt32(), "plugin.arguments_invalid", "Value too long.");
        }
        if (type is "integer" or "number")
        {
            Require(value.TryGetDecimal(out var n), "plugin.arguments_invalid", "Number outside supported range.");
            if (schema.TryGetProperty("minimum", out var min)) Require(n >= min.GetDecimal(), "plugin.arguments_invalid", "Number below minimum.");
            if (schema.TryGetProperty("maximum", out var max)) Require(n <= max.GetDecimal(), "plugin.arguments_invalid", "Number above maximum.");
        }
    }
    private static bool TryGetInt64Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
}
