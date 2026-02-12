using System.Text.Json;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 共享工具 helper：ADR-029 workspace 注入。
/// </summary>
internal static class MemoryToolHelper
{
    /// <summary>
    /// 序列化 Tool 参数并注入 Runtime WorkspaceId 和 AgentInstanceId。
    /// 优先使用 request.Context，不与 LLM 参数冲突。
    /// </summary>
    public static string SerializeWithWorkspace(ToolExecutionRequest request)
    {
        var merged = ExtractParameters(request.ArgumentsJson)
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.Context.WorkspaceId))
            merged["workspace_id"] = request.Context.WorkspaceId;

        // ADR-042: 注入 AgentInstanceId 用于 Agent 记忆隔离。
        if (!string.IsNullOrWhiteSpace(request.Context.AgentInstanceId))
            merged["agent_instance_id"] = request.Context.AgentInstanceId;

        return JsonSerializer.Serialize(merged);
    }

    public static IReadOnlyDictionary<string, string> ExtractParameters(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();

            return doc.RootElement.EnumerateObject()
                .Select(p => (p.Name, Value: ConvertJsonValueToParameterString(p.Value)))
                .Where(p => p.Value is not null)
                .ToDictionary(
                    p => p.Name,
                    p => p.Value!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static string ExtractInput(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name is "input" or "command" or "url" or "code" or "query" or "text" or "content")
                        return prop.Value.GetString() ?? string.Empty;
                }

                return root.GetRawText();
            }

            return root.ValueKind == JsonValueKind.String
                ? root.GetString() ?? string.Empty
                : root.GetRawText();
        }
        catch
        {
            return argumentsJson;
        }
    }

    private static string? ConvertJsonValueToParameterString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null,
        };
}
