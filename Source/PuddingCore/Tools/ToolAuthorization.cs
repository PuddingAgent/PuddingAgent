using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingCode.Tools;

/// <summary>Runtime action requested by a user-authored tool authorization command.</summary>
public enum ToolAuthorizationAction
{
    Authorize,
    Deny,
    Revoke,
}

/// <summary>High-level slash command category recognized by the system layer.</summary>
public enum SystemCommandKind
{
    Authorization,
    Help,
    Compact,
    Memory,
    Status,
    Stop,
    Mode,
    EmergencyStop,
    Resume,
    Yolo,
}

/// <summary>Action parsed from a user-authored slash command before routing.</summary>
public enum SystemCommandAction
{
    Authorize,
    Deny,
    Revoke,
    Help,
    Run,
}

/// <summary>Lifetime for a runtime tool authorization grant.</summary>
public enum ToolAuthorizationScope
{
    Timed,
    Once,
    Session,
    Permanent,
}

/// <summary>Parsed system slash command that must originate from a real user message.</summary>
public sealed record SystemCommand
{
    public required string RawText { get; init; }
    public required SystemCommandAction Action { get; init; }
    public required SystemCommandKind CommandKind { get; init; }
    public required string TargetId { get; init; }
    public ToolAuthorizationScope Scope { get; init; } = ToolAuthorizationScope.Timed;
    public TimeSpan Duration { get; init; } = ToolAuthorizationDefaults.DefaultDuration;

    public ToolAuthorizationCommand ToToolAuthorizationCommand()
        => new()
        {
            RawText = RawText,
            Action = Action switch
            {
                SystemCommandAction.Authorize => ToolAuthorizationAction.Authorize,
                SystemCommandAction.Deny => ToolAuthorizationAction.Deny,
                SystemCommandAction.Revoke => ToolAuthorizationAction.Revoke,
                _ => throw new InvalidOperationException($"System command action '{Action}' is not a tool authorization action."),
            },
            ToolId = TargetId,
            Scope = Scope,
            Duration = Duration,
        };
}

/// <summary>Parsed tool authorization command ready to be applied by the authorization service.</summary>
public sealed record ToolAuthorizationCommand
{
    public required string RawText { get; init; }
    public required ToolAuthorizationAction Action { get; init; }
    public required string ToolId { get; init; }
    public ToolAuthorizationScope Scope { get; init; } = ToolAuthorizationScope.Timed;
    public TimeSpan Duration { get; init; } = ToolAuthorizationDefaults.DefaultDuration;
}

/// <summary>Execution identity used to apply or verify tool authorization.</summary>
public sealed record ToolAuthorizationContext
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string UserId { get; init; }
    public required string ToolId { get; init; }
    public string? ArgumentsHash { get; init; }
}

/// <summary>Result returned to callers after applying a user authorization command.</summary>
public sealed record ToolAuthorizationCommandResult
{
    public required bool Handled { get; init; }
    public required string Message { get; init; }
}

/// <summary>Result of checking whether a high-risk tool call may proceed.</summary>
public sealed record ToolAuthorizationCheckResult
{
    public required bool IsAuthorized { get; init; }
    public required string Message { get; init; }
}

/// <summary>High-risk tool authorization service used by platform chat and runtime tool execution.</summary>
public interface IToolAuthorizationService
{
    Task<ToolAuthorizationCommandResult> ApplyCommandAsync(
        ToolAuthorizationCommand command,
        ToolAuthorizationContext context,
        CancellationToken ct = default);

    Task<ToolAuthorizationCheckResult> CheckAsync(
        ToolAuthorizationContext context,
        ToolDescriptor descriptor,
        CancellationToken ct = default);

    string BuildRequiredMessage(string toolId, ToolDescriptor? descriptor = null);
}

/// <summary>Default values and user-facing English command examples for tool authorization.</summary>
public static class ToolAuthorizationDefaults
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(10);

    public static string BuildAuthorizeCommand(string toolId, string suffix = "10m")
        => $"/authorize {NormalizeToolId(toolId)} {suffix}";

    public static string BuildRequiredMessage(string toolId, ToolDescriptor? descriptor = null)
    {
        var normalized = NormalizeToolId(toolId);
        var displayName = descriptor?.Name ?? normalized;
        return
            $"Runtime approval required for high-risk tool '{normalized}' ({displayName}). " +
            $"Recommended next step for the agent: call request_tool_approval with tool_id='{normalized}' and the exact planned arguments. " +
            "Use manual human authorization only as a fallback when automatic approval is unavailable or requires a human decision. " +
            "Fallback /authorize commands: " +
            $"{BuildAuthorizeCommand(normalized)}, " +
            $"{BuildAuthorizeCommand(normalized, "once")}, " +
            $"{BuildAuthorizeCommand(normalized, "session")}, " +
            $"{BuildAuthorizeCommand(normalized, "permanent")}.";
    }

    public static string BuildHelpMessage(string? commandName = null)
    {
        var normalized = NormalizeToolId(commandName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "System commands:" + Environment.NewLine + Environment.NewLine +
                   "- `/help` - Show system command help." + Environment.NewLine +
                   "- `/authorize <tool> [10m|1h|once|session|permanent]` - Manually grant runtime authorization for a high-risk tool when automatic approval cannot be used." + Environment.NewLine +
                   "- `/deny <tool>` - Deny authorization and clear active grants for that tool." + Environment.NewLine +
                   "- `/revoke <tool>` - Revoke active grants for that tool." + Environment.NewLine +
                   "- `/status` - Show runtime, session, model, tool, safety, resource, and recovery status." + Environment.NewLine +
                   "- `/stop` - Stop the current session." + Environment.NewLine +
                   "- `/stop all` - Stop all active sessions." + Environment.NewLine +
                   "- `/mode` - Show current runtime mode." + Environment.NewLine +
                   "- `/mode safe` - Enter safe mode and block Agent/Tool execution." + Environment.NewLine +
                   "- `/mode normal` - Exit safe mode." + Environment.NewLine +
                   "- `/mode list` - List runtime modes and examples." + Environment.NewLine +
                   "- `/estop` - Emergency stop the backend after writing a best-effort response." + Environment.NewLine +
                   "- `/compact` - Compact the current session context and refresh the current-day agent summary." + Environment.NewLine +
                   "- `/memory` - Manage or write memories. Current feature is not implemented.";
        }

        return normalized switch
        {
            "authorize" => "Help for `/authorize`:" + Environment.NewLine + Environment.NewLine +
                           "- `/authorize <tool> [10m|1h|once|session|permanent]` - Manual fallback for granting runtime authorization when automatic approval cannot be used." + Environment.NewLine +
                           "- `/authorize shell` - Grant shell for 10 minutes." + Environment.NewLine +
                           "- `/authorize file_write once` - Grant the next file write call." + Environment.NewLine +
                           "- `/authorize file_patch session` - Grant file patching for this session." + Environment.NewLine +
                           "- `/authorize shell once` - Grant shell for the next call in this session." + Environment.NewLine +
                           "- `/authorize shell session` - Grant shell for this session." + Environment.NewLine +
                           "- `/authorize shell permanent` - Grant shell permanently for this user and agent.",
            "deny" => "Help for `/deny`:" + Environment.NewLine + Environment.NewLine +
                      "- `/deny <tool>` - Deny authorization and clear active grants for that tool.",
            "revoke" => "Help for `/revoke`:" + Environment.NewLine + Environment.NewLine +
                        "- `/revoke <tool>` - Revoke active grants for that tool.",
            "compact" => "Help for `/compact`:" + Environment.NewLine + Environment.NewLine +
                         "- `/compact` - Compact the current session context and refresh the current-day agent summary.",
            "memory" => "Help for `/memory`:" + Environment.NewLine + Environment.NewLine +
                        "- `/memory` - Manage or write memories. Current feature is not implemented.",
            "status" => "Help for `/status`:" + Environment.NewLine + Environment.NewLine +
                        "- `/status` - Show the current Runtime, Session, Agent, Model, Skill, Tool, Safety, Resource, and Recovery snapshot.",
            "stop" => "Help for `/stop`:" + Environment.NewLine + Environment.NewLine +
                      "- `/stop` - Stop the current session." + Environment.NewLine +
                      "- `/stop all` - Stop all active sessions.",
            "mode" => "Help for `/mode`:" + Environment.NewLine + Environment.NewLine +
                      "- `/mode` - Show current runtime mode." + Environment.NewLine +
                      "- `/mode safe` - Enter safe mode." + Environment.NewLine +
                      "- `/mode normal` - Exit safe mode." + Environment.NewLine +
                      "- `/mode list` - List modes and examples.",
            "estop" => "Help for `/estop`:" + Environment.NewLine + Environment.NewLine +
                       "- `/estop` - Emergency stop the backend process after best-effort audit/log snapshot.",
            _ => $"Unknown system command '/{normalized}'. Send /help for available commands.",
        };
    }

    public static string NormalizeToolId(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    public static string ComputeArgumentsHash(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return string.Empty;

        var canonical = CanonicalizeArgumentsJson(argumentsJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static string CanonicalizeArgumentsJson(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalJson(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

}

/// <summary>
/// Parses strict English slash commands from user-authored chat messages.
/// Callers are responsible for only passing messages whose role is user.
/// </summary>
public static partial class SystemCommandParser
{
    public static bool TryParse(string? rawText, out SystemCommand command)
    {
        command = default!;

        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        var text = rawText.Trim();
        if (!text.StartsWith("/", StringComparison.Ordinal))
            return false;

        if (string.Equals(text, "/help", StringComparison.OrdinalIgnoreCase))
        {
            command = new SystemCommand
            {
                RawText = text,
                Action = SystemCommandAction.Help,
                CommandKind = SystemCommandKind.Help,
                TargetId = string.Empty,
            };
            return true;
        }

        var commandHelpMatch = CommandHelpRegex().Match(text);
        if (commandHelpMatch.Success)
        {
            command = new SystemCommand
            {
                RawText = text,
                Action = SystemCommandAction.Help,
                CommandKind = SystemCommandKind.Help,
                TargetId = ToolAuthorizationDefaults.NormalizeToolId(commandHelpMatch.Groups["command"].Value),
            };
            return true;
        }

        var systemCommandMatch = SystemCommandRegex().Match(text);
        if (systemCommandMatch.Success)
        {
            var commandName = ToolAuthorizationDefaults.NormalizeToolId(systemCommandMatch.Groups["command"].Value);
            var argument = ToolAuthorizationDefaults.NormalizeToolId(
                systemCommandMatch.Groups["argument"].Success
                    ? systemCommandMatch.Groups["argument"].Value
                    : string.Empty);
            if (!IsValidSystemCommandArgument(commandName, argument))
                return false;

            command = new SystemCommand
            {
                RawText = text,
                Action = SystemCommandAction.Run,
                TargetId = string.IsNullOrWhiteSpace(argument) ? commandName : argument,
                CommandKind = commandName switch
                {
                    "compact" => SystemCommandKind.Compact,
                    "memory" => SystemCommandKind.Memory,
                    "status" => SystemCommandKind.Status,
                    "stop" => SystemCommandKind.Stop,
                    "mode" => SystemCommandKind.Mode,
                    "estop" => SystemCommandKind.EmergencyStop,
                    "resume" => SystemCommandKind.Resume,
                    "yolo" => SystemCommandKind.Yolo,
                    _ => throw new InvalidOperationException($"Unsupported system command '{commandName}'."),
                },
            };
            return true;
        }

        var match = AuthorizationCommandRegex().Match(text);
        if (!match.Success)
            return false;

        var verb = match.Groups["verb"].Value.ToLowerInvariant();
        var toolId = ToolAuthorizationDefaults.NormalizeToolId(match.Groups["tool"].Value);
        if (string.IsNullOrWhiteSpace(toolId))
            return false;

        var action = verb switch
        {
            "authorize" => SystemCommandAction.Authorize,
            "deny" => SystemCommandAction.Deny,
            "revoke" => SystemCommandAction.Revoke,
            _ => throw new InvalidOperationException($"Unsupported authorization command '{verb}'."),
        };

        var scope = ToolAuthorizationScope.Timed;
        var duration = ToolAuthorizationDefaults.DefaultDuration;
        var qualifier = match.Groups["qualifier"].Success
            ? match.Groups["qualifier"].Value.Trim()
            : string.Empty;

        if (action == SystemCommandAction.Authorize && !string.IsNullOrWhiteSpace(qualifier))
        {
            if (string.Equals(qualifier, "once", StringComparison.OrdinalIgnoreCase))
            {
                scope = ToolAuthorizationScope.Once;
            }
            else if (string.Equals(qualifier, "session", StringComparison.OrdinalIgnoreCase))
            {
                scope = ToolAuthorizationScope.Session;
            }
            else if (string.Equals(qualifier, "permanent", StringComparison.OrdinalIgnoreCase))
            {
                scope = ToolAuthorizationScope.Permanent;
            }
            else if (TryParseDuration(qualifier, out var parsedDuration))
            {
                scope = ToolAuthorizationScope.Timed;
                duration = parsedDuration;
            }
            else
            {
                return false;
            }
        }
        else if (action != SystemCommandAction.Authorize && !string.IsNullOrWhiteSpace(qualifier))
        {
            return false;
        }

        command = new SystemCommand
        {
            RawText = text,
            Action = action,
            TargetId = toolId,
            CommandKind = SystemCommandKind.Authorization,
            Scope = scope,
            Duration = duration,
        };
        return true;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        var match = DurationRegex().Match(value.Trim());
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["amount"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0)
        {
            return false;
        }

        duration = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
            "h" or "hour" or "hours" => TimeSpan.FromHours(amount),
            _ => default,
        };
        return duration > TimeSpan.Zero;
    }

    [GeneratedRegex(@"^/(?<verb>authorize|deny|revoke)\s+(?<tool>[a-zA-Z0-9_]+)(?:\s+(?<qualifier>[a-zA-Z0-9]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationCommandRegex();

    private static bool IsValidSystemCommandArgument(string commandName, string argument)
        => commandName switch
        {
            "compact" or "memory" or "status" or "estop" or "resume" or "yolo" => string.IsNullOrWhiteSpace(argument),
            "stop" => string.IsNullOrWhiteSpace(argument) || string.Equals(argument, "all", StringComparison.Ordinal),
            "mode" => string.IsNullOrWhiteSpace(argument)
                      || argument is "safe" or "normal" or "list",
            _ => false,
        };

    [GeneratedRegex(@"^/(?<command>compact|memory|status|stop|mode|estop|resume|yolo)(?:\s+(?<argument>[a-zA-Z0-9_]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SystemCommandRegex();

    [GeneratedRegex(@"^/(?<command>[a-zA-Z0-9_-]+)\s+-help$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandHelpRegex();

    [GeneratedRegex(@"^(?<amount>[0-9]+)(?<unit>m|min|mins|minute|minutes|h|hour|hours)$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();
}
