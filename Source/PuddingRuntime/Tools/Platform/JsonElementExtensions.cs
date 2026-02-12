using System.Text.Json;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// System.Text.Json.JsonElement 扩展方法，供 Tool 实现使用。
/// </summary>
internal static class JsonElementExtensions
{
    public static string GetString(this JsonElement el, string name, string defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? defaultValue;
        return defaultValue;
    }

    public static string? GetOptionalString(this JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    /// <summary>容错版：非 string 类型也转换（number → ToString, true/false → "true"/"false"）。</summary>
    public static string? GetStringCoerced(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => prop.GetRawText(),
        };
    }

    public static int GetInt32(this JsonElement el, string name, int defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        if (el.TryGetProperty(name, out prop)
            && prop.ValueKind == JsonValueKind.String
            && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return defaultValue;
    }

    public static long? GetInt64(this JsonElement el, string name, long? defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();
        if (el.TryGetProperty(name, out prop)
            && prop.ValueKind == JsonValueKind.String
            && long.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return defaultValue;
    }

    public static bool GetBoolean(this JsonElement el, string name, bool defaultValue)
    {
        if (!el.TryGetProperty(name, out var prop))
            return defaultValue;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(prop.GetString(), out var parsed) ? parsed : defaultValue,
            _ => defaultValue,
        };
    }
}
