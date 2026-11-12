using System.Text.RegularExpressions;

namespace PuddingDingTalk;

/// <summary>Projects user-visible navigation metadata from reviewed MPCAL registrations.</summary>
public static partial class MpcalNavigationEvidence
{
    public static IReadOnlyList<string> Search(string reviewedSource, string question)
    {
        var results = new List<(int Score, string Path)>();
        foreach (Match definition in Registration().Matches(reviewedSource))
        {
            var block = definition.Groups[1].Value;
            var panel = Panel().Match(block).Groups[1].Value switch
                { "System" => "系统面板", "Project" => "项目面板", _ => null };
            var labels = DisplayField().Matches(block).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            if (panel is null || !labels.TryGetValue("Title", out var title) || !labels.TryGetValue("Category", out var category)) continue;
            var subtitle = labels.GetValueOrDefault("SubTitle");
            var score = Score(title, question) * 3 + Score(category, question) + Score(subtitle ?? "", question);
            if (score > 0) results.Add((score, $"{panel} → {category} → {title}" + (subtitle is null ? "" : $"（{subtitle}）")));
        }
        return results.OrderByDescending(x => x.Score).Select(x => x.Path).Distinct().Take(5).ToArray();
    }

    private static int Score(string label, string question)
    {
        if (label.Length < 2) return 0;
        if (question.Contains(label, StringComparison.OrdinalIgnoreCase)) return label.Length * 3;
        var hits = 0;
        for (var i = 1; i < label.Length; i++)
            if (question.Contains(label.Substring(i - 1, 2), StringComparison.OrdinalIgnoreCase)) hits++;
        return hits;
    }

    [GeneratedRegex(@"registry\.Register\(new FeatureDefinition\s*\{([^{}]+)\}\);", RegexOptions.CultureInvariant)]
    private static partial Regex Registration();
    [GeneratedRegex(@"^\s*PanelType\s*=\s*FeaturePanelType\.(\w+)", RegexOptions.Multiline)]
    private static partial Regex Panel();
    [GeneratedRegex("""^\s*(Title|Category|SubTitle)\s*=\s*"([^"\r\n]{1,64})"\s*,""", RegexOptions.Multiline)]
    private static partial Regex DisplayField();
}
