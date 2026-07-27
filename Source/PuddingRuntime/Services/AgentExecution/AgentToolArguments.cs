using System.Text.Json;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// Pure translation helpers between LLM tool-call JSON and the legacy skill invocation contract.
/// </summary>
internal static class AgentToolArguments
{
    private static readonly string[] InputKeys =
        ["command", "input", "query", "content", "text", "code", "url", "path"];

    public static string ExtractInput(JsonElement? args)
    {
        if (args is null)
            return string.Empty;

        var element = args.Value;
        if (element.ValueKind != JsonValueKind.Object)
            return element.GetRawText();

        foreach (var key in InputKeys)
        {
            if (element.TryGetProperty(key, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }
        }

        return element.GetRawText();
    }

    public static IReadOnlyDictionary<string, string> ExtractParameters(JsonElement? args)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        return args.Value.EnumerateObject()
            .Select(property => (property.Name, Value: ConvertToParameterString(property.Value)))
            .Where(property => property.Value is not null)
            .ToDictionary(
                property => property.Name,
                property => property.Value!,
                StringComparer.OrdinalIgnoreCase);
    }

    public static string ExtractInputFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return ExtractInput(root);

            return root.ValueKind == JsonValueKind.String
                ? root.GetString() ?? string.Empty
                : root.GetRawText();
        }
        catch
        {
            return argumentsJson;
        }
    }

    public static IReadOnlyDictionary<string, string> ExtractParametersFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new Dictionary<string, string>();

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return ExtractParameters(document.RootElement);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static string BuildTerminalExecutePayload(
        string processId,
        TerminalProcessInfo? finalInfo,
        string terminalOutput,
        int nextOffset)
    {
        var output = Truncate(terminalOutput, 2000);
        if (finalInfo is null)
        {
            return
                $"Tool 'terminal_execute' returned '{processId}', but no matching terminal process was found.\n" +
                $"Output:\n{output}";
        }

        if (finalInfo.Status == TerminalProcessStatus.Running)
        {
            return
                $"Tool 'terminal_execute' started background terminal job '{processId}'.\n" +
                $"Status: {finalInfo.Status}\n" +
                $"Next output offset: {nextOffset}\n" +
                $"Initial output:\n{output}\n" +
                "Do not wait for it in this turn. Use terminal_wait with job_id and from_offset to poll incremental output, or terminal_cancel to stop it.";
        }

        return $"Tool 'terminal_execute' exited with code={finalInfo.ExitCode}. Output:\n{output}";
    }

    private static string? ConvertToParameterString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null,
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
