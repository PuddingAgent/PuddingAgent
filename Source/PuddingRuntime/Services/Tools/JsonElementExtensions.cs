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

    public static int GetInt32(this JsonElement el, string name, int defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return defaultValue;
    }

    public static long? GetInt64(this JsonElement el, string name, long? defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();
        return defaultValue;
    }
}
