using System.Text.Json;

namespace PuddingDesktop.Core;

/// <summary>
/// Parses PUDDING_DESKTOP_READY lines from Core stdout.
/// </summary>
public static class CoreReadyMessageParser
{
    public const string ReadyPrefix = "PUDDING_DESKTOP_READY ";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Try to parse a line from Core stdout as a Ready message.
    /// Returns null if the line does not contain the ready prefix.
    /// Throws if the JSON is malformed or the address is invalid.
    /// Handles trailing text after the JSON object.
    /// </summary>
    public static CoreReadyMessage? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var idx = line.IndexOf(ReadyPrefix, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var afterPrefix = line[(idx + ReadyPrefix.Length)..].TrimStart();
        if (afterPrefix.Length == 0 || afterPrefix[0] != '{')
            return null;

        // Find the matching closing brace to extract just the JSON object
        var jsonEnd = FindJsonObjectEnd(afterPrefix);
        if (jsonEnd < 0)
            return null;

        var json = afterPrefix[..(jsonEnd + 1)];

        var raw = JsonSerializer.Deserialize<CoreReadyMessage.RawReadyMessage>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse PUDDING_DESKTOP_READY JSON.");

        if (string.IsNullOrWhiteSpace(raw.BaseAddress))
            throw new InvalidOperationException("PUDDING_DESKTOP_READY missing baseAddress.");

        var address = new Uri(raw.BaseAddress);

        if (!address.IsLoopback)
            throw new InvalidOperationException(
                $"Core control address must be loopback. Got: {raw.BaseAddress}");

        if (address.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException(
                $"Core must use HTTP. Got: {address.Scheme}");

        return new CoreReadyMessage
        {
            ProtocolVersion = raw.ProtocolVersion,
            ProcessId = raw.ProcessId,
            BaseAddress = address,
        };
    }

    /// <summary>
    /// Finds the end of a JSON object (matching '}') starting at position 0.
    /// Handles nested braces and strings.
    /// Returns -1 if no matching brace found.
    /// </summary>
    private static int FindJsonObjectEnd(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\' && i + 1 < text.Length)
                {
                    i++; // skip escaped char
                    continue;
                }
                if (ch == '"')
                    inString = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }
        return -1;
    }
}
