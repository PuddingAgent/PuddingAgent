using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEval.Services;

/// <summary>
/// Renders an <see cref="EvalRun"/> as Markdown (human reading) and JSON (machine comparison for
/// regression). <b>No thresholds and no verdicts are emitted</b> — the report states raw numbers and
/// says so explicitly, because the acceptance line is a product decision, not an instrument constant.
/// </summary>
public static class EvalReportWriter
{
    /// <summary>Default JSON options: indented, enums as names, stable ordering.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serialises a run for machine comparison. Two runs of the same revision must produce
    /// identical JSON except for timestamps and latency samples.</summary>
    public static string ToJson(EvalRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return JsonSerializer.Serialize(run, JsonOptions);
    }

    /// <summary>Writes <c>&lt;basePath&gt;.md</c> and <c>&lt;basePath&gt;.json</c> as UTF-8 without BOM.</summary>
    public static void Write(string basePath, EvalRun run, string? reproduceCommand = null)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new ArgumentException("basePath is required", nameof(basePath));

        ArgumentNullException.ThrowIfNull(run);

        var directory = Path.GetDirectoryName(Path.GetFullPath(basePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        File.WriteAllText(basePath + ".md", ToMarkdown(run, reproduceCommand), utf8NoBom);
        File.WriteAllText(basePath + ".json", ToJson(run), utf8NoBom);
    }

    /// <summary>Markdown report: headline aggregates, language strata, latency buckets, then every raw case.</summary>
    public static string ToMarkdown(EvalRun run, string? reproduceCommand = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var text = new StringBuilder();

        text.AppendLine($"# Retrieval evaluation — {run.ProbeName}");
        text.AppendLine();
        text.AppendLine("> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**");
        text.AppendLine("> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.");
        text.AppendLine();

        text.AppendLine("## Run");
        text.AppendLine();
        text.AppendLine("| field | value |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| probe | `{run.ProbeName}` |");
        text.AppendLine($"| set | `{run.SetName}` v{run.SetVersion.ToString(CultureInfo.InvariantCulture)} |");
        text.AppendLine($"| scope label | `{run.ScopeLabel}` |");
        text.AppendLine($"| scope root | `{run.ScopeRootDirectory}` |");
        text.AppendLine($"| started (UTC) | {run.StartedAtUtc} |");
        text.AppendLine($"| total elapsed (ms) | {run.TotalElapsedMs.ToString(CultureInfo.InvariantCulture)} |");
        text.AppendLine($"| cases | {run.CaseCount.ToString(CultureInfo.InvariantCulture)} |");
        text.AppendLine($"| failed calls | {run.FailedCaseCount.ToString(CultureInfo.InvariantCulture)} |");
        text.AppendLine($"| repetition-stable | {(run.AllRepetitionsIdentical ? "yes" : "NO — accuracy numbers are not reproducible")} |");
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(reproduceCommand))
        {
            text.AppendLine("## Reproduce");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(reproduceCommand.Trim());
            text.AppendLine("```");
            text.AppendLine();
        }

        text.AppendLine("## Accuracy (mean over cases)");
        text.AppendLine();
        text.AppendLine("| metric | value |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| recall@1 | {Ratio(run.RecallAt1)} |");
        text.AppendLine($"| recall@5 | {Ratio(run.RecallAt5)} |");
        text.AppendLine($"| recall@10 | {Ratio(run.RecallAt10)} |");
        text.AppendLine($"| MRR | {Ratio(run.Mrr)} |");
        text.AppendLine($"| precision@5 | {Ratio(run.PrecisionAt5)} |");
        text.AppendLine($"| precision@10 | {Ratio(run.PrecisionAt10)} |");
        text.AppendLine($"| noiseRate@10 | {Ratio(run.NoiseRateAt10)} |");
        text.AppendLine();

        text.AppendLine("## By language stratum");
        text.AppendLine();
        text.AppendLine("| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var slice in run.Slices)
        {
            text.AppendLine(
                $"| {slice.Language} | {slice.CaseCount.ToString(CultureInfo.InvariantCulture)} | {Ratio(slice.RecallAt1)} | "
                + $"{Ratio(slice.RecallAt5)} | {Ratio(slice.RecallAt10)} | {Ratio(slice.Mrr)} | "
                + $"{Ratio(slice.PrecisionAt5)} | {Ratio(slice.PrecisionAt10)} | {Ratio(slice.NoiseRateAt10)} |");
        }

        text.AppendLine();
        text.AppendLine("## Latency (raw ms)");
        text.AppendLine();
        text.AppendLine("| bucket | samples | min | p50 | p95 | p99 | max | mean |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        AppendLatencyRow(text, "cold (1st call per query)", run.ColdLatency);
        AppendLatencyRow(text, "warm (all later calls)", run.WarmLatency);
        text.AppendLine();
        text.AppendLine("Cold = first call for that query inside this process; warm = every later call.");
        text.AppendLine();

        text.AppendLine("## Cold-call raw samples");
        text.AppendLine();
        text.AppendLine("| # | ms | hits | ok | error |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var observation in run.ColdObservations)
        {
            text.AppendLine(
                $"| {observation.Index.ToString(CultureInfo.InvariantCulture)} | "
                + $"{observation.ElapsedMs.ToString(CultureInfo.InvariantCulture)} | "
                + $"{observation.HitCount.ToString(CultureInfo.InvariantCulture)} | "
                + $"{(observation.Success ? "yes" : "NO")} | {Cell(observation.Error)} |");
        }

        text.AppendLine();
        text.AppendLine("## Per-case results");
        text.AppendLine();
        text.AppendLine(
            "| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        var index = 0;
        foreach (var score in run.Cases)
        {
            text.AppendLine(
                $"| {index.ToString(CultureInfo.InvariantCulture)} | {score.Case.Kind} | {score.Case.Language} | {Cell(score.Case.Query)} | "
                + $"{score.MatchedExpectedCount.ToString(CultureInfo.InvariantCulture)}/{score.ExpectedCount.ToString(CultureInfo.InvariantCulture)} | "
                + $"{Ratio(score.RecallAt1)} | {Ratio(score.RecallAt5)} | {Ratio(score.RecallAt10)} | {Ratio(score.Mrr)} | "
                + $"{Ratio(score.PrecisionAt5)} | {Ratio(score.PrecisionAt10)} | {Ratio(score.NoiseRateAt10)} | "
                + $"{score.DistinctHitsAt10.ToString(CultureInfo.InvariantCulture)} | "
                + $"{score.NoiseHitsAt10.ToString(CultureInfo.InvariantCulture)} | "
                + $"{score.ElapsedMs.ToString(CultureInfo.InvariantCulture)} | {(score.Success ? "yes" : "NO")} |");
            index++;
        }

        text.AppendLine();
        text.AppendLine("### Expected hits per case");
        text.AppendLine();
        text.AppendLine("| # | query | expected | returned (ranked, top 10) |");
        text.AppendLine("|---|---|---|---|");

        index = 0;
        foreach (var score in run.Cases)
        {
            var expected = string.Join("<br>", score.Case.ExpectedHits.Select(e => "`" + Cell(e) + "`"));
            var returned = score.RankedHits.Count == 0
                ? "_(none)_"
                : string.Join("<br>", score.RankedHits.Take(RetrievalMetrics.K10).Select(h => "`" + Cell(h.Path) + "`"));
            text.AppendLine($"| {index.ToString(CultureInfo.InvariantCulture)} | {Cell(score.Case.Query)} | {expected} | {returned} |");
            index++;
        }

        return text.ToString();
    }

    private static void AppendLatencyRow(StringBuilder text, string bucket, LatencySummary summary)
    {
        if (summary.Count == 0)
        {
            text.AppendLine($"| {bucket} | 0 | — | — | — | — | — | — |");
            return;
        }

        text.AppendLine(
            $"| {bucket} | {summary.Count.ToString(CultureInfo.InvariantCulture)} | "
            + $"{summary.MinMs.ToString(CultureInfo.InvariantCulture)} | {summary.P50Ms.ToString(CultureInfo.InvariantCulture)} | "
            + $"{summary.P95Ms.ToString(CultureInfo.InvariantCulture)} | {summary.P99Ms.ToString(CultureInfo.InvariantCulture)} | "
            + $"{summary.MaxMs.ToString(CultureInfo.InvariantCulture)} | "
            + $"{summary.MeanMs.ToString("0.##", CultureInfo.InvariantCulture)} |");
    }

    private static string Ratio(double value) =>
        value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var flattened = value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        return flattened.Length <= 120 ? flattened : flattened[..117] + "...";
    }
}
