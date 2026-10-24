using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PuddingTokenization;

/// <summary>
/// Expands the Unicode general categories used by DeepSeek's Split expressions to include
/// supplementary scalars. .NET Regex otherwise treats a surrogate pair as two non-category chars.
/// Only matching changes; BPE still receives the original UTF-16 text and UTF-8 bytes.
/// </summary>
internal static class ScalarRegex
{
    private const string Scalar = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\uD800-\uDFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF])";
    private static readonly ConcurrentDictionary<char, string> Supplementary = new();
    private static readonly Regex Categories = new(@"\\p\{([LNMPS])\}");

    public static Regex Create(string pattern)
    {
        var expanded = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            if (pattern[i] == '[')
            {
                var end = i + 1;
                for (; end < pattern.Length; end++)
                {
                    if (pattern[end] == '\\') { end++; continue; }
                    if (pattern[end] == ']') break;
                }
                if (end == pattern.Length)
                    throw new ArgumentException("Unclosed Split character class.", nameof(pattern));
                var characterClass = pattern[i..(end + 1)];
                var groups = Categories.Matches(characterClass).Select(m => m.Groups[1].Value[0]).Distinct().ToArray();
                if (groups.Length == 0)
                    expanded.Append(characterClass);
                else
                {
                    var supplementary = string.Join('|', groups.Select(GetSupplementary));
                    if (characterClass[1] == '^')
                    {
                        // Negation excludes whole scalars, never half of a surrogate pair.
                        expanded.Append("(?:(?![").Append(characterClass.AsSpan(2))
                            .Append('|').Append(supplementary).Append(')').Append(Scalar).Append(')');
                    }
                    else
                        expanded.Append("(?:").Append(characterClass).Append('|').Append(supplementary).Append(')');
                }
                i = end + 1;
            }
            else if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                var match = Categories.Match(pattern, i);
                if (match.Success && match.Index == i)
                {
                    expanded.Append("(?:").Append(match.Value).Append('|')
                        .Append(GetSupplementary(match.Groups[1].Value[0])).Append(')');
                    i += match.Length;
                }
                else
                {
                    expanded.Append(pattern.AsSpan(i, 2));
                    i += 2;
                }
            }
            else
                expanded.Append(pattern[i++]);
        }
        return new Regex(expanded.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    private static string GetSupplementary(char group) => Supplementary.GetOrAdd(group, static category =>
    {
        var alternatives = new List<string>();
        // Each alternative stays under one high surrogate, with contiguous low-surrogate ranges.
        for (var high = 0xD800; high <= 0xDBFF; high++)
        {
            var lows = new StringBuilder();
            var start = -1;
            for (var low = 0xDC00; low <= 0xE000; low++)
            {
                var included = low < 0xE000 && BelongsTo(
                    Rune.GetUnicodeCategory(new Rune((char)high, (char)low)), category);
                if (included && start < 0) start = low;
                if (!included && start >= 0)
                {
                    lows.Append($"\\u{start:X4}");
                    if (low - 1 > start) lows.Append($"-\\u{low - 1:X4}");
                    start = -1;
                }
            }
            if (lows.Length > 0) alternatives.Add($"\\u{high:X4}[{lows}]");
        }
        return string.Join('|', alternatives);
    });

    private static bool BelongsTo(UnicodeCategory category, char group) => group switch
    {
        'L' => category is >= UnicodeCategory.UppercaseLetter and <= UnicodeCategory.OtherLetter,
        'M' => category is >= UnicodeCategory.NonSpacingMark and <= UnicodeCategory.EnclosingMark,
        'N' => category is >= UnicodeCategory.DecimalDigitNumber and <= UnicodeCategory.OtherNumber,
        'P' => category is >= UnicodeCategory.ConnectorPunctuation and <= UnicodeCategory.OtherPunctuation,
        'S' => category is >= UnicodeCategory.MathSymbol and <= UnicodeCategory.OtherSymbol,
        _ => false,
    };
}
