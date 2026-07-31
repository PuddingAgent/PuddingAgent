using System.Text.RegularExpressions;

namespace PuddingCode.Models;

/// <summary>
/// Provider- and channel-neutral projection of Markdown <c>voice</c> fences.
/// The canonical Agent reply remains unchanged; channel projectors decide how
/// to render the extracted text and speech parts.
/// </summary>
public sealed record AgentReplyVoiceDirectiveResult
{
    public required string OriginalContent { get; init; }
    public required string TextContent { get; init; }
    public required string VoiceContent { get; init; }
    public required string TextFallbackContent { get; init; }

    public bool HasVoice => !string.IsNullOrWhiteSpace(VoiceContent);
    public bool HasText => !string.IsNullOrWhiteSpace(TextContent);
    public bool IsVoiceOnly => HasVoice && !HasText;
}

public static class AgentReplyVoiceDirective
{
    private static readonly Regex VoiceBlockRegex = new(
        @"^[ \t]*```voice[ \t]*\r?\n(?<voice>.*?)(?:\r?\n)?^[ \t]*```[ \t]*(?:\r?\n|$)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Multiline
        | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ExcessBlankLinesRegex = new(
        @"(?:\r?\n)[ \t]*(?:\r?\n[ \t]*){2,}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static AgentReplyVoiceDirectiveResult Parse(string? content)
    {
        var original = content ?? string.Empty;
        var matches = VoiceBlockRegex.Matches(original);
        if (matches.Count == 0
            || matches.Cast<Match>().Any(match =>
                string.IsNullOrWhiteSpace(match.Groups["voice"].Value)))
        {
            return NoDirective(original);
        }

        var voice = string.Join(
            Environment.NewLine + Environment.NewLine,
            matches
                .Cast<Match>()
                .Select(match => match.Groups["voice"].Value.Trim()));
        var text = NormalizeProjectedText(
            VoiceBlockRegex.Replace(original, string.Empty));
        var fallback = NormalizeProjectedText(
            VoiceBlockRegex.Replace(
                original,
                match => $"{match.Groups["voice"].Value.Trim()}\n"));

        return new AgentReplyVoiceDirectiveResult
        {
            OriginalContent = original,
            TextContent = text,
            VoiceContent = voice,
            TextFallbackContent = fallback,
        };
    }

    private static AgentReplyVoiceDirectiveResult NoDirective(string content)
        => new()
        {
            OriginalContent = content,
            TextContent = content.Trim(),
            VoiceContent = string.Empty,
            TextFallbackContent = content.Trim(),
        };

    private static string NormalizeProjectedText(string content)
        => ExcessBlankLinesRegex.Replace(content.Trim(), "\n\n");
}
