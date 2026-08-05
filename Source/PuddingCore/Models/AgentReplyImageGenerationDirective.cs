using System.Text.RegularExpressions;

namespace PuddingCode.Models;

public sealed record AgentReplyImageGenerationItem
{
    public required int Index { get; init; }
    public required string Prompt { get; init; }
    public string Mode { get; init; } = "default";
    public string Size { get; init; } = "2K";
    public bool Watermark { get; init; } = true;
    public string OutputFormat { get; init; } = "png";
    public string OptimizePromptMode { get; init; } = "standard";
    public bool EnableWebSearch { get; init; }
    public int ImageCount { get; init; } = 1;
    public IReadOnlyList<string> ReferenceArtifactIds { get; init; } = [];
}

public sealed record AgentReplyImageGenerationDirectiveResult
{
    public required string OriginalContent { get; init; }
    public required IReadOnlyList<AgentReplyImageGenerationItem> Items { get; init; }
    public bool HasImageGeneration => Items.Count > 0;
    public int TotalImageCount => Items.Sum(item => item.ImageCount);
}

/// <summary>
/// Parses provider-neutral Markdown ImageGeneration fences. The original reply
/// is never rewritten; Feishu V1 keeps the fence visible and appends images.
/// </summary>
public static class AgentReplyImageGenerationDirective
{
    public const int MaxBlocks = 4;
    public const int MaxImages = 4;

    private static readonly Regex BlockRegex = new(
        @"^[ \t]*```ImageGeneration[ \t]*\r?\n(?<body>.*?)(?:\r?\n)?^[ \t]*```[ \t]*(?:\r?\n|$)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Multiline
        | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex HeaderRegex = new(
        @"^[ \t]*(?<key>[a-zA-Z_]+)[ \t]*:[ \t]*(?<value>.*?)[ \t]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ArtifactIdRegex = new(
        @"^vision-[a-f0-9]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static AgentReplyImageGenerationDirectiveResult Parse(string? content)
    {
        var original = content ?? string.Empty;
        var items = new List<AgentReplyImageGenerationItem>();
        var totalImages = 0;
        var matches = BlockRegex.Matches(original);
        foreach (Match match in matches.Cast<Match>().Take(MaxBlocks))
        {
            var item = ParseItem(match.Groups["body"].Value, items.Count);
            if (item is null || totalImages + item.ImageCount > MaxImages)
                continue;

            items.Add(item);
            totalImages += item.ImageCount;
        }

        return new AgentReplyImageGenerationDirectiveResult
        {
            OriginalContent = original,
            Items = items,
        };
    }

    /// <summary>
    /// Removes ImageGeneration fences from a reply so channel projectors can
    /// deliver the surrounding text without leaking the raw directive code.
    /// Only the first <see cref="MaxBlocks"/> fences are removed, matching
    /// <see cref="Parse(string?)"/>.
    /// </summary>
    public static string StripBlocks(string? content)
    {
        var original = content ?? string.Empty;
        var result = original;
        foreach (Match match in BlockRegex.Matches(original)
                     .Cast<Match>()
                     .Take(MaxBlocks)
                     .OrderByDescending(match => match.Index))
        {
            result = result.Remove(match.Index, match.Length);
        }
        return result;
    }

    private static AgentReplyImageGenerationItem? ParseItem(
        string body,
        int index)
    {
        var normalized = body.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var lines = normalized.Split('\n');
        var headers = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var promptStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                promptStart = i + 1;
                break;
            }

            var header = HeaderRegex.Match(lines[i]);
            if (!header.Success || !IsKnownHeader(header.Groups["key"].Value))
            {
                promptStart = i;
                break;
            }

            headers[header.Groups["key"].Value] =
                header.Groups["value"].Value.Trim();
            promptStart = i + 1;
        }

        var prompt = string.Join('\n', lines[promptStart..]).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var mode = Get(headers, "mode") ?? "default";
        if (mode is not ("default" or "precision" or "sequence"))
            return null;

        var size = Get(headers, "size") ?? "2K";
        var outputFormat =
            Get(headers, "output_format")
            ?? Get(headers, "format")
            ?? "png";
        if (outputFormat is not ("png" or "jpeg"))
            return null;

        var optimize =
            Get(headers, "optimize")
            ?? Get(headers, "optimize_prompt_mode")
            ?? "standard";
        if (optimize is not ("standard" or "fast"))
            return null;

        if (!TryBoolean(Get(headers, "watermark"), defaultValue: true, out var watermark)
            || !TryBoolean(
                Get(headers, "web_search"),
                defaultValue: false,
                out var webSearch))
        {
            return null;
        }

        var countValue = Get(headers, "count");
        if (countValue is not null
            && (!int.TryParse(countValue, out var parsedCount)
                || parsedCount is < 1 or > MaxImages))
        {
            return null;
        }
        var count = countValue is null ? 1 : int.Parse(countValue);

        var references = ParseReferences(
            Get(headers, "references")
            ?? Get(headers, "reference_artifact_ids"));
        if (references is null)
            return null;

        return new AgentReplyImageGenerationItem
        {
            Index = index,
            Prompt = prompt,
            Mode = mode,
            Size = size,
            Watermark = watermark,
            OutputFormat = outputFormat,
            OptimizePromptMode = optimize,
            EnableWebSearch = webSearch,
            ImageCount = count,
            ReferenceArtifactIds = references,
        };
    }

    private static IReadOnlyList<string>? ParseReferences(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var references = value
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return references.Count <= 10
               && references.All(item => ArtifactIdRegex.IsMatch(item))
            ? references
            : null;
    }

    private static bool TryBoolean(
        string? value,
        bool defaultValue,
        out bool result)
    {
        if (value is null)
        {
            result = defaultValue;
            return true;
        }

        if (bool.TryParse(value, out result))
            return true;
        if (value is "1" or "0")
        {
            result = value == "1";
            return true;
        }

        return false;
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> headers,
        string key)
        => headers.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : null;

    private static bool IsKnownHeader(string key)
        => key.ToLowerInvariant() is
            "mode"
            or "size"
            or "watermark"
            or "output_format"
            or "format"
            or "optimize"
            or "optimize_prompt_mode"
            or "web_search"
            or "count"
            or "references"
            or "reference_artifact_ids";
}
