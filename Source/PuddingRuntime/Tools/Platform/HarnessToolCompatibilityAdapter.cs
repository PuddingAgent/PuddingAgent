using System.Text.Json;
using System.Text.Json.Nodes;

namespace PuddingRuntime.Services;

/// <summary>
/// Deterministic execution-boundary compatibility for common model-training harness contracts.
/// Canonical Pudding tool descriptors remain the source of truth; aliases are normalized before
/// authorization, hashing, telemetry, and execution so compatibility cannot bypass policy gates.
/// </summary>
internal static class HarnessToolCompatibilityAdapter
{
    private static readonly IReadOnlyDictionary<string, string> ToolAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["exec_command"] = "shell",
            ["run_command"] = "shell",
            ["rg"] = "search_grep",
            ["grep"] = "search_grep",
            ["read_file"] = "file_read",
            ["write_file"] = "file_write",
            ["list_directory"] = "list_dir",
            ["write_stdin"] = "terminal_input",
        };

    internal static HarnessToolInvocation Normalize(string toolName, string? argumentsJson)
    {
        var requestedToolName = toolName?.Trim() ?? string.Empty;
        var canonicalToolName = ToolAliases.TryGetValue(requestedToolName, out var alias)
            ? alias
            : requestedToolName;
        var normalizedArguments = NormalizeArguments(canonicalToolName, argumentsJson, out var argumentsAdapted);
        var toolAdapted = !canonicalToolName.Equals(requestedToolName, StringComparison.Ordinal);

        return new HarnessToolInvocation(
            requestedToolName,
            canonicalToolName,
            argumentsJson ?? string.Empty,
            normalizedArguments,
            toolAdapted || argumentsAdapted,
            toolAdapted,
            argumentsAdapted);
    }

    internal static bool IsExpectedNoMatchExit(string? command, int exitCode, string? output)
    {
        if (exitCode != 1 || string.IsNullOrWhiteSpace(command))
            return false;

        var executable = ReadFirstExecutable(command);
        if (executable is not ("rg" or "grep" or "findstr"))
            return false;

        var diagnostic = output ?? string.Empty;
        return !ContainsAny(
            diagnostic,
            "CommandNotFoundException",
            "not recognized as the name",
            "is not recognized as an internal or external command",
            "No such file or directory",
            "command not found",
            "无法将",
            "找不到");
    }

    internal static bool IsRipgrepCommand(string? command)
        => string.Equals(ReadFirstExecutable(command), "rg", StringComparison.Ordinal);

    private static string NormalizeArguments(
        string toolName,
        string? argumentsJson,
        out bool adapted)
    {
        adapted = false;
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return argumentsJson ?? string.Empty;

        if (toolName is "apply_patch" or "file_patch"
            && LooksLikeRawPatch(argumentsJson))
        {
            adapted = true;
            return JsonSerializer.Serialize(new { patch_text = argumentsJson });
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(argumentsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return argumentsJson;
        }

        if (root is null)
            return argumentsJson;

        switch (toolName)
        {
            case "search_grep":
                NormalizeSearchArguments(root, ref adapted);
                break;
            case "shell":
                MoveAlias(root, "command", ref adapted, "cmd");
                MoveAlias(root, "working_directory", ref adapted, "workdir", "cwd");
                MoveMilliseconds(root, "timeout_seconds", ref adapted, "timeout_ms");
                NormalizeShellName(root, ref adapted);
                break;
            case "terminal_start":
                MoveAlias(root, "command", ref adapted, "cmd");
                MoveAlias(root, "cwd", ref adapted, "workdir", "working_directory");
                MoveTokenBudget(root, "max_output_chars", ref adapted, "max_output_tokens");
                break;
            case "terminal_wait":
                MoveAlias(root, "job_id", ref adapted, "session_id", "process_id");
                MoveMilliseconds(root, "wait_seconds", ref adapted, "yield_time_ms");
                MoveAlias(root, "max_chars", ref adapted, "max_output_chars");
                break;
            case "terminal_read":
            case "terminal_status":
            case "terminal_cancel":
                MoveAlias(root, "job_id", ref adapted, "session_id", "process_id");
                break;
            case "terminal_input":
                MoveAlias(root, "job_id", ref adapted, "session_id", "process_id");
                MoveAlias(root, "input", ref adapted, "chars");
                break;
            case "apply_patch":
            case "file_patch":
                MoveAlias(root, "patch_text", ref adapted, "patch", "input");
                break;
        }

        return adapted ? root.ToJsonString() : argumentsJson;
    }

    private static void NormalizeSearchArguments(JsonObject root, ref bool adapted)
    {
        var hasCanonicalQuery = TryFind(root, "query", out _, out _);
        var patternWasQuery = false;
        if (!hasCanonicalQuery)
        {
            if (TryFind(root, "regex", out var regexKey, out var regexValue)
                || TryFind(root, "needle", out regexKey, out regexValue)
                || TryFind(root, "search", out regexKey, out regexValue))
            {
                root["query"] = regexValue?.DeepClone();
                root.Remove(regexKey!);
                adapted = true;
            }
            else if (TryFind(root, "pattern", out var patternKey, out var patternValue))
            {
                // Common rg-shaped function contracts call the search expression "pattern".
                // Pudding reserves pattern for the file glob, so consume it as query only when
                // the canonical query is absent.
                root["query"] = patternValue?.DeepClone();
                root.Remove(patternKey!);
                patternWasQuery = true;
                adapted = true;
            }
        }

        MoveAlias(root, "directory", ref adapted, "path", "root", "cwd", "workdir", "working_directory");
        MoveAlias(root, "max_results", ref adapted, "limit", "max_count", "head_limit");
        MoveAlias(root, "case_sensitive", ref adapted, "caseSensitive");
        if (patternWasQuery || !TryFind(root, "pattern", out _, out _))
            MoveAlias(root, "pattern", ref adapted, "glob", "include", "file_glob");
    }

    private static void NormalizeShellName(JsonObject root, ref bool adapted)
    {
        if (!TryFind(root, "shell", out var key, out var value)
            || value is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var shell))
        {
            return;
        }

        if (shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || shell.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            root[key!] = "powershell";
            adapted = true;
        }
        else if (shell.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)
                 || shell.Equals("linux", StringComparison.OrdinalIgnoreCase)
                 || shell.Equals("unix", StringComparison.OrdinalIgnoreCase)
                 || shell.Equals("ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            root[key!] = "wsl";
            adapted = true;
        }
    }

    private static void MoveAlias(
        JsonObject root,
        string canonical,
        ref bool adapted,
        params string[] aliases)
    {
        if (TryFind(root, canonical, out _, out _))
        {
            RemoveAliases(root, ref adapted, aliases);
            return;
        }

        foreach (var alias in aliases)
        {
            if (!TryFind(root, alias, out var key, out var value))
                continue;

            root[canonical] = value?.DeepClone();
            root.Remove(key!);
            adapted = true;
            RemoveAliases(root, ref adapted, aliases);
            return;
        }
    }

    private static void MoveMilliseconds(
        JsonObject root,
        string canonical,
        ref bool adapted,
        string alias)
    {
        if (TryFind(root, canonical, out _, out _)
            || !TryFind(root, alias, out var key, out var value)
            || !TryReadLong(value, out var milliseconds))
        {
            return;
        }

        root[canonical] = (int)Math.Clamp((milliseconds + 999) / 1000, 0, 600);
        root.Remove(key!);
        adapted = true;
    }

    private static void MoveTokenBudget(
        JsonObject root,
        string canonical,
        ref bool adapted,
        string alias)
    {
        if (TryFind(root, canonical, out _, out _)
            || !TryFind(root, alias, out var key, out var value)
            || !TryReadLong(value, out var tokens))
        {
            return;
        }

        var boundedTokens = Math.Clamp(tokens, 1L, 50_000L);
        root[canonical] = (int)(boundedTokens * 4);
        root.Remove(key!);
        adapted = true;
    }

    private static void RemoveAliases(JsonObject root, ref bool adapted, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (TryFind(root, alias, out var key, out _) && root.Remove(key!))
                adapted = true;
        }
    }

    private static bool TryFind(
        JsonObject root,
        string name,
        out string? actualKey,
        out JsonNode? value)
    {
        foreach (var property in root)
        {
            if (!property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            actualKey = property.Key;
            value = property.Value;
            return true;
        }

        actualKey = null;
        value = null;
        return false;
    }

    private static bool TryReadLong(JsonNode? node, out long value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out value))
                return true;
            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                value = intValue;
                return true;
            }
            if (jsonValue.TryGetValue<string>(out var text) && long.TryParse(text, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static bool LooksLikeRawPatch(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("*** Begin Patch", StringComparison.Ordinal)
               || trimmed.StartsWith("--- ", StringComparison.Ordinal);
    }

    private static string? ReadFirstExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.TrimStart();
        if (trimmed.StartsWith('&'))
            trimmed = trimmed[1..].TrimStart();

        string token;
        if (trimmed.StartsWith('"') || trimmed.StartsWith('\''))
        {
            var quote = trimmed[0];
            var end = trimmed.IndexOf(quote, 1);
            token = end > 1 ? trimmed[1..end] : trimmed[1..];
        }
        else
        {
            var end = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            token = end < 0 ? trimmed : trimmed[..end];
        }

        token = Path.GetFileName(token.Trim());
        if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || token.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || token.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            token = Path.GetFileNameWithoutExtension(token);
        }

        return token.ToLowerInvariant();
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

internal sealed record HarnessToolInvocation(
    string RequestedToolName,
    string ToolName,
    string RequestedArgumentsJson,
    string ArgumentsJson,
    bool Adapted,
    bool ToolNameAdapted,
    bool ArgumentsAdapted);
