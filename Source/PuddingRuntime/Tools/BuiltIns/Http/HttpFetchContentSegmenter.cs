using System.Text;

namespace PuddingRuntime.Services.Skills;

/// <summary>One page of content produced by <see cref="HttpFetchContentSegmenter"/>.</summary>
/// <param name="Text">The page body.</param>
/// <param name="StartOffset">Character offset the page starts at.</param>
/// <param name="NextOffset">Character offset the next page starts at; equals the content length when done.</param>
/// <param name="HasMore">Whether unread content remains after this page.</param>
public sealed record HttpFetchContentPage(string Text, int StartOffset, int NextOffset, bool HasMore);

/// <summary>
/// Splits long fetched content into readable pages so the agent can pull only as much as it needs.
/// </summary>
/// <remarks>
/// The alternative - one hard cut at the character limit - throws away structure and forces the
/// agent to re-fetch the whole page to see what came next. Cutting on a heading or blank-line
/// boundary instead keeps every page semantically whole, and a cursor lets the agent resume.
/// </remarks>
public static class HttpFetchContentSegmenter
{
    /// <summary>
    /// Takes at most <paramref name="maxChars"/> characters starting at <paramref name="offset"/>,
    /// preferring a heading or paragraph boundary over a raw character cut.
    /// </summary>
    public static HttpFetchContentPage Slice(string content, int offset, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxChars <= 0)
            maxChars = 1;

        var start = Math.Clamp(offset, 0, content.Length);
        if (start >= content.Length)
            return new HttpFetchContentPage(string.Empty, start, content.Length, HasMore: false);

        if (content.Length - start <= maxChars)
        {
            return new HttpFetchContentPage(content[start..], start, content.Length, HasMore: false);
        }

        var windowEnd = start + maxChars;
        var cut = FindBreakOffset(content, start, windowEnd);
        return new HttpFetchContentPage(content[start..cut], start, cut, HasMore: true);
    }

    /// <summary>
    /// Builds a heading outline so the agent can decide what to read before pulling any body text.
    /// </summary>
    /// <param name="content">Markdown content to outline.</param>
    /// <param name="maxHeadings">Cap on headings listed.</param>
    public static string BuildOutline(string content, int maxHeadings = 60)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxHeadings <= 0)
            maxHeadings = 1;

        var headings = new List<(int Level, string Text)>();
        var truncated = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!TryParseHeading(line, out var level, out var text))
                continue;

            if (headings.Count >= maxHeadings)
            {
                truncated = true;
                break;
            }

            headings.Add((level, text));
        }

        var sb = new StringBuilder();
        sb.Append("Outline: ").Append(headings.Count).Append(headings.Count == 1 ? " heading" : " headings");
        sb.Append(", ").Append(content.Length).AppendLine(" chars total.");

        if (headings.Count == 0)
        {
            sb.AppendLine("(no markdown headings found; read body text directly)");
            return sb.ToString();
        }

        foreach (var (level, text) in headings)
        {
            sb.Append(' ', Math.Max(0, level - 1) * 2).Append("- ").Append(text).AppendLine();
        }

        if (truncated)
            sb.AppendLine("... [outline truncated]");

        return sb.ToString();
    }

    /// <summary>
    /// Prefers a heading start, then a blank line, and only then a raw cut. A boundary is used only
    /// when it keeps at least half the budget, otherwise the page would be wastefully short.
    /// </summary>
    private static int FindBreakOffset(string content, int start, int windowEnd)
    {
        var floor = start + Math.Max(1, (windowEnd - start) / 2);
        var count = windowEnd - start;

        var heading = content.LastIndexOf('\n', windowEnd - 1, count);
        while (heading >= 0)
        {
            // Only a "\n#" sequence starts a heading; walk back until we find one or leave the window.
            if (heading + 1 < content.Length && content[heading + 1] == '#' && heading >= floor)
                return heading + 1;

            if (heading <= start)
                break;

            var nextCount = heading - start;
            if (nextCount <= 0)
                break;

            heading = content.LastIndexOf('\n', heading - 1, nextCount);
        }

        var blank = content.LastIndexOf("\n\n", windowEnd - 1, count, StringComparison.Ordinal);
        if (blank >= floor)
            return blank + 2;

        return windowEnd;
    }

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        while (level < line.Length && line[level] == '#')
            level++;

        if (level is < 1 or > 6)
            return false;

        if (level >= line.Length || line[level] != ' ')
            return false;

        text = line[(level + 1)..].Trim();
        return text.Length > 0;
    }
}
