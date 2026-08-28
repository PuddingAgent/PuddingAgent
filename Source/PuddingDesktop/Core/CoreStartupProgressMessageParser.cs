using System.Text.Json;

namespace PuddingDesktop.Core;

/// <summary>
/// Parses the structured startup-lease protocol from Core stdout.
/// </summary>
public static class CoreStartupProgressMessageParser
{
    public const string ProgressPrefix = "PUDDING_DESKTOP_STARTING ";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static CoreStartupProgressMessage? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var index = line.IndexOf(ProgressPrefix, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var afterPrefix = line[(index + ProgressPrefix.Length)..].TrimStart();
        if (afterPrefix.Length == 0 || afterPrefix[0] != '{')
            return null;

        var jsonEnd = CoreReadyMessageParser.FindJsonObjectEnd(afterPrefix);
        if (jsonEnd < 0)
            return null;

        var raw = JsonSerializer.Deserialize<CoreStartupProgressMessage.RawProgressMessage>(
                afterPrefix[..(jsonEnd + 1)],
                JsonOptions)
            ?? throw new InvalidOperationException(
                "Failed to parse PUDDING_DESKTOP_STARTING JSON.");

        if (raw.Sequence < 1)
            throw new InvalidOperationException(
                "PUDDING_DESKTOP_STARTING sequence must be positive.");
        if (string.IsNullOrWhiteSpace(raw.Phase))
            throw new InvalidOperationException(
                "PUDDING_DESKTOP_STARTING missing phase.");

        return new CoreStartupProgressMessage
        {
            ProtocolVersion = raw.ProtocolVersion,
            ProcessId = raw.ProcessId,
            Sequence = raw.Sequence,
            Phase = raw.Phase,
            ElapsedMilliseconds = raw.ElapsedMilliseconds,
        };
    }
}
