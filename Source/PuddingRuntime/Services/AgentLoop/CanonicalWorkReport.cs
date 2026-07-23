using System.Text;
using System.Text.RegularExpressions;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Canonical five-section work-report contract shared by delegated Agent runs and
/// Smart workflow validation.
/// </summary>
internal static class CanonicalWorkReport
{
    internal const string ExpectedOutputContract =
        "SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS";

    internal const int MinimumDetailedReportLength = 80;
    private const int MinimumSummaryLength = 20;
    private const int MinimumEvidenceLength = 20;

    internal static readonly string[] RequiredSections =
        ["SUMMARY", "CHANGES", "EVIDENCE", "RISKS", "BLOCKERS"];

    internal static bool IsRequiredBy(string? expectedOutputContract)
    {
        if (string.IsNullOrWhiteSpace(expectedOutputContract))
            return false;

        return RequiredSections.All(section => Regex.IsMatch(
            expectedOutputContract,
            $@"(?<![A-Z0-9_]){Regex.Escape(section)}(?![A-Z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    internal static bool TryValidate(string? report, out string error)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            error = "rawOutput is empty.";
            return false;
        }

        var trimmed = report.Trim();
        if (trimmed.Length < MinimumDetailedReportLength)
        {
            error =
                $"report is too short ({trimmed.Length} chars; minimum {MinimumDetailedReportLength}).";
            return false;
        }

        var sections = ParseSections(report);
        var missing = RequiredSections
            .Where(section => !sections.TryGetValue(section, out var content)
                              || string.IsNullOrWhiteSpace(content))
            .ToArray();
        if (missing.Length > 0)
        {
            error = $"missing or empty canonical sections: {string.Join(", ", missing)}.";
            return false;
        }

        var summaryLength = sections.GetValueOrDefault("SUMMARY", string.Empty).Length;
        if (summaryLength < MinimumSummaryLength)
        {
            error =
                $"SUMMARY is too short ({summaryLength} chars; minimum {MinimumSummaryLength}).";
            return false;
        }

        var evidenceLength = sections.GetValueOrDefault("EVIDENCE", string.Empty).Length;
        if (evidenceLength < MinimumEvidenceLength)
        {
            error =
                $"EVIDENCE is too short ({evidenceLength} chars; minimum {MinimumEvidenceLength}).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static IReadOnlyDictionary<string, string> ParseSections(string report)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var content = new StringBuilder();

        foreach (var line in report.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var matched = RequiredSections.FirstOrDefault(section =>
                trimmed.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                if (current is not null)
                    sections[current] = content.ToString().Trim();

                current = matched;
                content.Clear();
                var inline = trimmed[(matched.Length + 1)..].Trim();
                if (inline.Length > 0)
                    content.AppendLine(inline);
                continue;
            }

            if (current is not null)
                content.AppendLine(line);
        }

        if (current is not null)
            sections[current] = content.ToString().Trim();

        return sections;
    }
}

/// <summary>
/// Keeps the latest contract-complete delegated result so a later status-only
/// DONE envelope cannot replace an already delivered report.
/// </summary>
internal sealed class ExpectedOutputCandidateTracker
{
    private readonly bool _requiresCanonicalWorkReport;
    private string? _lastValidCanonicalReport;

    internal ExpectedOutputCandidateTracker(string? expectedOutputContract)
    {
        _requiresCanonicalWorkReport =
            CanonicalWorkReport.IsRequiredBy(expectedOutputContract);
    }

    internal bool Observe(string? candidate)
    {
        if (!_requiresCanonicalWorkReport
            || !CanonicalWorkReport.TryValidate(candidate, out _))
        {
            return false;
        }

        _lastValidCanonicalReport = candidate!.Trim();
        return true;
    }

    internal bool RestoreIfFinalIsIncomplete(ref string finalMessage)
    {
        if (!_requiresCanonicalWorkReport
            || string.IsNullOrWhiteSpace(_lastValidCanonicalReport)
            || CanonicalWorkReport.TryValidate(finalMessage, out _))
        {
            return false;
        }

        finalMessage = _lastValidCanonicalReport;
        return true;
    }
}
