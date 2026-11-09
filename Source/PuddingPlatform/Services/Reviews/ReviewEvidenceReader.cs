using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5: the objective/criteria text for a logical review key comes from the frozen acceptance contract
/// (host-bound), never from chat text. A missing contract means the review must wait for a snapshot.
/// </summary>
public interface IReviewObjectiveSource
{
    ValueTask<(string Objective, ImmutableArray<ReviewCriterion> Criteria)?> LoadAsync(ReviewLogicalKey key,
        CancellationToken ct = default);
}

/// <summary>
/// PA-03 S5 evidence reader: only finished controlled check reports for the job's goal run/epoch become
/// EvidenceKind.CheckReport items (content hash over the stored report JSON); pending/leased records and goals
/// without a contract are never counted as evidence. A criterion without a finished report is reported as a
/// missing reason and keeps EvidenceComplete=false, so the result validator cannot pass it.
/// </summary>
public sealed class ReviewEvidenceReader(GoalCheckRecordStore checks, IReviewObjectiveSource objectives)
    : IReviewEvidenceReader
{
    public async ValueTask<ReviewEvidenceReadResult> ReadAsync(ReviewJobView job, CancellationToken ct = default)
    {
        if (job is null) throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A review job is required.");
        if (job.Key.GoalRunId is not { } goalRunId)
            return new(null, ReviewErrorCodes.SnapshotRequired);
        var objective = await objectives.LoadAsync(job.Key, ct).ConfigureAwait(false);
        if (objective is not { } contract || string.IsNullOrWhiteSpace(contract.Objective) || contract.Criteria.IsDefault)
            return new(null, ReviewErrorCodes.SnapshotRequired);
        var records = await checks.ReadForEpochAsync(goalRunId, (int)job.Key.ActivationEpoch, ct).ConfigureAwait(false);
        var evidence = ImmutableArray.CreateBuilder<ReviewEvidenceItem>();
        var versionRejected = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Status != GoalCheckRecordStatuses.Finished || string.IsNullOrWhiteSpace(record.ReportJson))
                continue;
            if (!MatchesFrozenVersions(job.Key, record.ReportJson, out var mismatchReason))
            {
                versionRejected.Add($"evidence-version-mismatch:{record.CriterionId}:{mismatchReason}");
                continue;
            }
            covered.Add(record.CriterionId);
            var excerpt = Excerpt(record.ReportJson);
            evidence.Add(new ReviewEvidenceItem(record.CheckRecordId, EvidenceKind.CheckReport,
                $"check://{record.CheckId}", record.CheckRecordId, Hash(record.ReportJson), record.UpdatedAtUtc,
                "controlled-check", Truncated: false, ExitCode(record.ReportJson), excerpt.Content,
                excerpt.Truncated, excerpt.UnavailableReason));
        }
        var missing = contract.Criteria.Where(c => !covered.Contains(c.CriterionId))
            .Select(c => $"criterion-not-checked:{c.CriterionId}").Concat(versionRejected).ToList();
        var snapshot = new ReviewInputSnapshot(job.ReviewId, job.Key, contract.Objective, contract.Criteria,
            evidence.ToImmutable(), EvidenceComplete: missing.Count == 0, [.. missing],
            PromptVersion: job.Key.ReviewPolicyVersion, ModelPolicyVersion: job.Key.ReviewPolicyVersion,
            job.BudgetAccountId, job.Deadline, job.ActorId);
        return new(snapshot, null);
    }

    /// <summary>
    /// A historical exit 0 is only usable when its report declares the same frozen objective/acceptance/artifact
    /// versions as the job key. A mismatch is reported as a missing reason; it is never silently accepted.
    /// </summary>
    private static bool MatchesFrozenVersions(ReviewLogicalKey key, string reportJson, out string mismatchReason)
    {
        mismatchReason = "";
        try
        {
            using var document = JsonDocument.Parse(reportJson);
            var root = document.RootElement;
            foreach (var (property, expected) in new[]
                     {
                         ("objectiveVersion", key.ObjectiveVersion),
                         ("acceptanceContractVersion", key.AcceptanceContractVersion),
                         ("artifactSetHash", key.ArtifactSetHash),
                     })
            {
                if (!root.TryGetProperty(property, out var value)) continue;
                var actual = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
                if (actual is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    mismatchReason = property;
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            mismatchReason = "report-unreadable";
            return false;
        }
    }

    private const int MaxExcerptChars = 4096;

    /// <summary>Bounded, authorized report body for the model; metadata alone cannot prove acceptance semantics.</summary>
    private static (string? Content, bool Truncated, string? UnavailableReason) Excerpt(string reportJson)
    {
        try
        {
            using var document = JsonDocument.Parse(reportJson);
            var root = document.RootElement;
            foreach (var property in new[] { "summary", "output", "body", "text", "message" })
            {
                if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()!;
                    return text.Length <= MaxExcerptChars
                        ? (text, false, null)
                        : (text[..MaxExcerptChars], true, null);
                }
            }
            var raw = root.GetRawText();
            return raw.Length <= MaxExcerptChars ? (raw, false, null) : (raw[..MaxExcerptChars], true, null);
        }
        catch (JsonException)
        {
            return (null, false, "report-unreadable");
        }
    }
    private static string? ExitCode(string reportJson)
    {
        try
        {
            using var document = JsonDocument.Parse(reportJson);
            if (document.RootElement.TryGetProperty("exitCode", out var code))
                return code.ValueKind switch
                {
                    JsonValueKind.Number => code.GetRawText(),
                    JsonValueKind.String => code.GetString(),
                    _ => null,
                };
        }
        catch (JsonException) { }
        return null;
    }

    private static string Hash(string reportJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reportJson)));
}
