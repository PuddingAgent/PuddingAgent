using System.Text.RegularExpressions;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// P2-4: CSS-aware matching helpers for FilePatchTool.
/// Collapses whitespace and structural characters so that &quot;color : red ;&quot;
/// matches &quot;color:red;&quot; regardless of formatting differences.
/// </summary>
internal static class CssPatchHelpers
{
    /// <summary>
    /// CSS-aware character normalization — collapses whitespace AND structural delimiters.
    /// </summary>
    internal static string? NormalizeCssChar(char c)
    {
        if (char.IsWhiteSpace(c)) return null;
        if (c is '{' or '}' or ';' or ',' or ':') return null;
        return c.ToString();
    }

    /// <summary>
    /// Lightweight heuristic: returns true when text contains CSS-like tokens
    /// (property:value pairs, selectors with {}, vendor prefixes).
    /// Requires at least 2 of 3 signals to avoid false positives.
    /// </summary>
    internal static bool LooksLikeCssSource(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hits = 0;
        if (Regex.IsMatch(text, @"[-\w]+\s*:\s*[^;{}]+")) hits++;
        if (Regex.IsMatch(text, @"[.#]?[-\w]+\s*\{", RegexOptions.None)) hits++;
        if (Regex.IsMatch(text, @"-webkit-|-moz-|-ms-|-o-")) hits++;
        return hits >= 2;
    }
}
