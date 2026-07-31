using System.Text.RegularExpressions;

namespace PuddingCode.Models;

public sealed record AgentReplyImageItem
{
    public required int Index { get; init; }
    public required string Reference { get; init; }
    public required int MatchIndex { get; init; }
    public required int MatchLength { get; init; }
}

public sealed record AgentReplyImageDirectiveResult
{
    public required string OriginalContent { get; init; }
    public required IReadOnlyList<AgentReplyImageItem> Items { get; init; }
    public bool HasImages => Items.Count > 0;
    public bool IsPureImage { get; init; }
}

/// <summary>
/// Parses explicit Markdown image fences that point at an existing workspace
/// Vision Artifact. Filesystem authorization remains a Platform responsibility.
/// </summary>
public static class AgentReplyImageDirective
{
    public const int MaxImages = 4;

    private static readonly Regex BlockRegex = new(
        @"^[ \t]*```image[ \t]*\r?\n(?<body>.*?)(?:\r?\n)?^[ \t]*```[ \t]*(?:\r?\n|$)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Multiline
        | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ArtifactIdRegex = new(
        @"^vision-[a-f0-9]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static AgentReplyImageDirectiveResult Parse(string? content)
    {
        var original = content ?? string.Empty;
        var items = new List<AgentReplyImageItem>();
        foreach (Match match in BlockRegex.Matches(original)
                     .Cast<Match>()
                     .Take(MaxImages))
        {
            var reference = NormalizeReference(match.Groups["body"].Value);
            if (reference is null)
                continue;

            items.Add(new AgentReplyImageItem
            {
                Index = items.Count,
                Reference = reference,
                MatchIndex = match.Index,
                MatchLength = match.Length,
            });
        }

        var isPure = items.Count == 1
                     && string.Equals(
                         original.Trim(),
                         original.Substring(
                             items[0].MatchIndex,
                             items[0].MatchLength).Trim(),
                         StringComparison.Ordinal);
        return new AgentReplyImageDirectiveResult
        {
            OriginalContent = original,
            Items = items,
            IsPureImage = isPure,
        };
    }

    /// <summary>
    /// Streaming projections use this to avoid publishing a temporary text card
    /// while a reply that starts with an image-only fence is still incomplete.
    /// </summary>
    public static bool CouldBePureImagePrefix(string? content)
    {
        var candidate = (content ?? string.Empty).TrimStart();
        const string marker = "```image";
        if (candidate.Length == 0)
            return false;
        if (candidate.Length < marker.Length)
            return marker.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
        if (!candidate.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            return false;
        if (candidate.Length == marker.Length)
            return true;

        var parsed = Parse(candidate);
        if (parsed.HasImages)
            return parsed.IsPureImage;

        return candidate[marker.Length] is ' ' or '\t' or '\r' or '\n';
    }

    private static string? NormalizeReference(string body)
    {
        var lines = body
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length != 1)
            return null;

        var value = Unquote(lines[0]);
        if (ArtifactIdRegex.IsMatch(value))
            return value.ToLowerInvariant();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return null;
        }
        if (!Path.IsPathFullyQualified(value))
            return null;

        var extension = Path.GetExtension(value);
        if (extension is not (
                ".jpg" or ".jpeg" or ".png" or ".webp"
                or ".JPG" or ".JPEG" or ".PNG" or ".WEBP"))
        {
            return null;
        }
        return ArtifactIdRegex.IsMatch(Path.GetFileNameWithoutExtension(value))
            ? value
            : null;
    }

    private static string Unquote(string value)
        => value.Length >= 2
           && ((value[0] == '"' && value[^1] == '"')
               || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1].Trim()
            : value;
}
