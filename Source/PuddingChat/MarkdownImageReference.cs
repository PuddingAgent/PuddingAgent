using System.Text.RegularExpressions;

namespace PuddingChat;

/// <summary>Maps presentation references to workspace-owned artifacts; never resolves a filesystem path.</summary>
public static partial class MarkdownImageReference
{
    public static string? Resolve(string reference, string workspace)
    {
        var value = reference.Trim();
        if (value.Length is 0 or > 4096 || value.Contains('\n') || value.Contains('\r')) return null;
        var apiPrefix = "/api/workspaces/";
        if (value.StartsWith(apiPrefix, StringComparison.Ordinal))
        {
            var expected = apiPrefix + Uri.EscapeDataString(workspace) + "/vision-artifacts/";
            return value.StartsWith(expected, StringComparison.Ordinal) && Id().IsMatch(value[expected.Length..])
                ? value[expected.Length..].ToLowerInvariant() : null;
        }
        if (value.Contains("://", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)) return null;
        if (Id().IsMatch(value)) return value.ToLowerInvariant();
        var match = FileReference().Match(value);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"\Avision-[a-f0-9]{32}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Id();
    [GeneratedRegex(@"(?:\A|[\\/])(vision-[a-f0-9]{32})\.(?:jpe?g|png|webp)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileReference();
}
