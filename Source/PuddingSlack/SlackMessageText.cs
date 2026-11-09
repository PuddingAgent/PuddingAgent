using System.Text;
using System.Text.RegularExpressions;

namespace PuddingSlack;

/// <summary>Allow explicit member mentions; never turn names or broadcast syntax into notifications.</summary>
public static partial class SlackMessageText
{
    [GeneratedRegex(@"<@[UW][A-Z0-9]+>", RegexOptions.CultureInvariant)]
    private static partial Regex MemberMention();

    public static string Format(string text)
    {
        var result = new StringBuilder(); var offset = 0;
        foreach (Match mention in MemberMention().Matches(text))
        {
            result.Append(Escape(text[offset..mention.Index]));
            result.Append(mention.Value); offset = mention.Index + mention.Length;
        }
        result.Append(Escape(text[offset..]));
        return result.ToString();
    }
    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
