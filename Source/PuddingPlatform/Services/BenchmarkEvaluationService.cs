using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatform.Services;

/// <summary>
/// Deterministically evaluates one benchmark run from workspace artifacts and persisted runtime facts.
/// It intentionally does not use an LLM judge, so the same evidence always produces the same result.
/// </summary>
public sealed class BenchmarkEvaluationService
{
    private const int MaxArtifactTextBytes = 1_000_000;

    private readonly PuddingDataPaths _paths;
    private readonly PlatformDbContext _db;
    private readonly BenchmarkRunService _runs;
    private readonly SessionBenchmarkDiagnosticsService _diagnostics;
    private readonly TimeProvider _timeProvider;

    public BenchmarkEvaluationService(
        PuddingDataPaths paths,
        PlatformDbContext db,
        BenchmarkRunService runs,
        SessionBenchmarkDiagnosticsService diagnostics,
        TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _db = db;
        _runs = runs;
        _diagnostics = diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BenchmarkEvaluationResultDto?> EvaluateAsync(
        string runId,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var run = await _runs.GetAsync(runId, ct);
        if (run is null)
            return null;

        var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId) ? run.SessionId : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(resolvedSessionId))
            throw new InvalidOperationException("Benchmark run has no session id; supply one when evaluating.");

        var diagnostics = await _diagnostics.BuildAsync(resolvedSessionId, 50, ct);
        var metrics = await BuildMetricsAsync(resolvedSessionId, diagnostics, ct);
        var checks = EvaluateContract(run, metrics);
        var instructionChecks = checks.Where(check => check.Category == "instruction").ToList();
        var efficiencyChecks = checks.Where(check => check.Category == "efficiency").ToList();
        var reliabilityChecks = checks.Where(check => check.Category == "reliability").ToList();

        int? instructionScore = instructionChecks.Count == 0
            ? null
            : (int)Math.Round(instructionChecks.Average(check => check.Score));
        var efficiencyScore = efficiencyChecks.Count == 0
            ? 100
            : (int)Math.Round(efficiencyChecks.Average(check => check.Score));
        var reliabilityScore = reliabilityChecks.Count == 0
            ? diagnostics.Scores.ToolExecution
            : (int)Math.Round(reliabilityChecks.Average(check => check.Score));

        var overallScore = instructionScore is null
            ? (int?)null
            : (int)Math.Round(
                instructionScore.Value * 0.60
                + efficiencyScore * 0.20
                + reliabilityScore * 0.20);
        var status = instructionScore is null
            ? "unscored"
            : checks.All(check => check.Passed) ? "passed" : "failed";

        var result = new BenchmarkEvaluationResultDto
        {
            RunId = run.RunId,
            CaseId = run.CaseId,
            CaseVersion = run.CaseVersion,
            CaseConfigHash = run.CaseConfigHash,
            WorkspaceId = run.WorkspaceId,
            SessionId = resolvedSessionId,
            Status = status,
            InstructionScore = instructionScore,
            EfficiencyScore = efficiencyScore,
            ReliabilityScore = reliabilityScore,
            OverallScore = overallScore,
            Metrics = metrics,
            Checks = checks,
            DiagnosticScores = diagnostics.Scores,
            EvaluatedAtUtc = _timeProvider.GetUtcNow(),
        };

        await _runs.SaveEvaluationAsync(result, ct);
        return result;
    }

    private async Task<BenchmarkObservedMetricsDto> BuildMetricsAsync(
        string sessionId,
        SessionBenchmarkReportDto diagnostics,
        CancellationToken ct)
    {
        var usageEvents = await _db.TokenUsageEvents
            .AsNoTracking()
            .Where(evt => evt.SessionId == sessionId || evt.ParentSessionId == sessionId)
            .ToListAsync(ct);
        usageEvents = usageEvents
            .OrderBy(evt => evt.OccurredAtUtc)
            .ToList();
        var commands = await _db.ChatExecutionCommands
            .AsNoTracking()
            .Where(command => command.SessionId == sessionId)
            .ToListAsync(ct);
        var subAgentRuns = await _db.SubAgentRuns
            .AsNoTracking()
            .Where(run => run.ParentSessionId == sessionId)
            .ToListAsync(ct);
        var subAgentRoutes = subAgentRuns
            .GroupBy(run => run.SubSessionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ParseSubAgentRoute(group.OrderByDescending(run => run.Id).First()),
                StringComparer.Ordinal);

        var startedAt = commands
            .Where(command => command.StartedAt.HasValue)
            .Select(command => command.StartedAt!.Value)
            .DefaultIfEmpty()
            .Min();
        var completedAt = commands
            .Where(command => command.CompletedAt.HasValue)
            .Select(command => command.CompletedAt!.Value)
            .DefaultIfEmpty()
            .Max();
        long? durationMs = startedAt > 0 && completedAt >= startedAt
            ? completedAt - startedAt
            : null;

        var promptTokens = usageEvents.Count > 0
            ? usageEvents.Sum(evt => evt.PromptTokens)
            : diagnostics.Usage.PromptTokens ?? 0;
        var completionTokens = usageEvents.Count > 0
            ? usageEvents.Sum(evt => evt.CompletionTokens)
            : diagnostics.Usage.CompletionTokens ?? 0;
        var totalTokens = usageEvents.Count > 0
            ? usageEvents.Sum(evt => evt.TotalTokens)
            : diagnostics.Usage.TotalTokens ?? promptTokens + completionTokens;
        var cacheHitTokens = usageEvents.Count > 0
            ? usageEvents.Sum(evt => evt.CacheHitTokens)
            : diagnostics.Usage.PromptCacheHitTokens ?? 0;
        var cacheMissTokens = usageEvents.Count > 0
            ? usageEvents.Sum(evt => evt.CacheMissTokens)
            : diagnostics.Usage.PromptCacheMissTokens ?? 0;
        var cacheDenominator = cacheHitTokens + cacheMissTokens;
        var explicitRounds = usageEvents
            .Where(evt => evt.TurnRound.HasValue)
            .Select(evt => evt.TurnRound!.Value + 1)
            .DefaultIfEmpty(0)
            .Max();
        var latestCommand = commands
            .OrderByDescending(command => command.CreatedAt)
            .ThenByDescending(command => command.Id)
            .FirstOrDefault();

        var modelUsage = usageEvents
            .Select(evt => new
            {
                Event = evt,
                Route = subAgentRoutes.GetValueOrDefault(evt.SessionId ?? string.Empty),
            })
            .GroupBy(item => new
            {
                Provider = item.Event.ProviderId ?? "unknown",
                Model = item.Event.ModelId ?? "unknown",
                Scope = string.IsNullOrWhiteSpace(item.Event.SubAgentId) ? "main" : "subagent",
                Role = item.Route?.RoleId,
                Profile = item.Route?.ProfileId,
                Agent = item.Route?.AgentInstanceId,
            })
            .Select(group => new BenchmarkModelUsageDto
            {
                ProviderId = group.Key.Provider,
                ModelId = group.Key.Model,
                Scope = group.Key.Scope,
                RoleId = group.Key.Role,
                ProfileId = group.Key.Profile,
                AgentInstanceId = group.Key.Agent,
                Calls = group.Count(),
                PromptTokens = group.Sum(item => item.Event.PromptTokens),
                CompletionTokens = group.Sum(item => item.Event.CompletionTokens),
                TotalTokens = group.Sum(item => item.Event.TotalTokens),
                CacheHitTokens = group.Sum(item => item.Event.CacheHitTokens),
                CostCny = group.Sum(item => item.Event.TotalCost),
            })
            .OrderBy(item => item.Scope, StringComparer.Ordinal)
            .ThenBy(item => item.ModelId, StringComparer.Ordinal)
            .ToList();

        return new BenchmarkObservedMetricsDto
        {
            DurationMs = durationMs,
            LlmCalls = usageEvents.Count,
            Rounds = explicitRounds > 0 ? explicitRounds : usageEvents.Count,
            TerminalStatus = latestCommand?.Status,
            ToolCalls = diagnostics.Counts.ToolCalls.Values.Sum(),
            FailedToolResults = diagnostics.Counts.FailedToolResults,
            BlockingToolFailures = diagnostics.Failures.Count(result =>
                !string.Equals(result.Category, "expected_failure", StringComparison.Ordinal)),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            CacheHitTokens = cacheHitTokens,
            CacheMissTokens = cacheMissTokens,
            CacheHitRate = cacheDenominator == 0 ? null : cacheHitTokens * 1.0 / cacheDenominator,
            CostCny = usageEvents.Sum(evt => evt.TotalCost),
            ModelUsage = modelUsage,
        };
    }

    private IReadOnlyList<BenchmarkEvaluationCheckDto> EvaluateContract(
        BenchmarkRunDto run,
        BenchmarkObservedMetricsDto metrics)
    {
        var contract = run.Evaluation;
        if (contract is null)
            return [];

        var checks = contract.Artifacts
            .Select(expectation => EvaluateArtifact(run, expectation))
            .ToList();

        checks.Add(new BenchmarkEvaluationCheckDto
        {
            Id = "terminal-status",
            Category = "reliability",
            Passed = string.Equals(metrics.TerminalStatus, "succeeded", StringComparison.OrdinalIgnoreCase),
            Score = string.Equals(metrics.TerminalStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? 100 : 0,
            Evidence = $"command status={metrics.TerminalStatus ?? "unavailable"}; expected=succeeded",
        });

        AddLimitCheck(checks, "duration", "efficiency", metrics.DurationMs, contract.MaxDurationSeconds is null
            ? null
            : contract.MaxDurationSeconds.Value * 1000L, "ms");
        AddLimitCheck(checks, "rounds", "efficiency", metrics.Rounds, contract.MaxRounds, "rounds");
        AddLimitCheck(checks, "tokens", "efficiency", metrics.TotalTokens, contract.MaxTotalTokens, "tokens");
        AddLimitCheck(checks, "cost", "efficiency", metrics.CostCny, contract.MaxCostCny, "CNY");
        AddLimitCheck<int>(
            checks,
            "failed-tools",
            "reliability",
            metrics.BlockingToolFailures,
            contract.MaxFailedToolResults,
            "blocking failures");
        return checks;
    }

    private BenchmarkEvaluationCheckDto EvaluateArtifact(
        BenchmarkRunDto run,
        BenchmarkArtifactExpectation expectation)
    {
        var pattern = NormalizeRelativePattern(expectation.Path);
        if (pattern is null)
        {
            return FailedArtifactCheck(expectation.Path, "unsafe artifact path pattern");
        }

        var workspacesRoot = Path.GetFullPath(_paths.WorkspacesRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var workspaceRoot = Path.GetFullPath(_paths.WorkspaceRoot(run.WorkspaceId));
        var workspaceBoundary = workspaceRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!workspaceBoundary.StartsWith(workspacesRoot, StringComparison.OrdinalIgnoreCase))
            return FailedArtifactCheck(expectation.Path, "workspace path escapes data root");
        if (!Directory.Exists(workspaceRoot))
            return FailedArtifactCheck(expectation.Path, "workspace does not exist");

        var matcher = BuildGlobRegex(pattern);
        var candidates = Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => matcher.IsMatch(Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/')))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
            return FailedArtifactCheck(expectation.Path, "no matching file");

        var failures = new List<string>();
        foreach (var path in candidates)
        {
            var file = new FileInfo(path);
            if (file.Length < expectation.MinBytes)
            {
                failures.Add($"{file.Name}: {file.Length} bytes");
                continue;
            }

            if (expectation.MustBeModifiedAfterRun
                && file.LastWriteTimeUtc < run.CreatedAtUtc.UtcDateTime.AddSeconds(-2))
            {
                failures.Add($"{file.Name}: stale timestamp");
                continue;
            }

            var content = file.Length <= MaxArtifactTextBytes
                ? File.ReadAllText(path)
                : string.Empty;
            var missing = expectation.RequiredContents
                .Where(term => !content.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var forbidden = expectation.ForbiddenContents
                .Where(term => content.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count == 0 && forbidden.Count == 0)
            {
                return new BenchmarkEvaluationCheckDto
                {
                    Id = $"artifact:{expectation.Path}",
                    Category = "instruction",
                    Passed = true,
                    Score = 100,
                    Evidence = $"matched {Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/')} ({file.Length} bytes)",
                };
            }

            if (missing.Count > 0)
                failures.Add($"{file.Name}: missing [{string.Join(", ", missing)}]");
            if (forbidden.Count > 0)
                failures.Add($"{file.Name}: forbidden [{string.Join(", ", forbidden)}]");
        }

        return FailedArtifactCheck(expectation.Path, string.Join("; ", failures.Take(4)));
    }

    private static void AddLimitCheck<T>(
        ICollection<BenchmarkEvaluationCheckDto> checks,
        string id,
        string category,
        T? actual,
        T? limit,
        string unit)
        where T : struct, IComparable<T>, IConvertible
    {
        if (limit is null)
            return;

        if (actual is null)
        {
            checks.Add(new BenchmarkEvaluationCheckDto
            {
                Id = id,
                Category = category,
                Passed = false,
                Score = 0,
                Evidence = $"metric unavailable; limit={limit} {unit}",
            });
            return;
        }

        var actualNumber = Convert.ToDecimal(actual.Value);
        var limitNumber = Convert.ToDecimal(limit.Value);
        var passed = actual.Value.CompareTo(limit.Value) <= 0;
        var score = passed || actualNumber <= 0
            ? 100
            : (int)Math.Clamp(Math.Round(limitNumber / actualNumber * 100), 0, 100);
        checks.Add(new BenchmarkEvaluationCheckDto
        {
            Id = id,
            Category = category,
            Passed = passed,
            Score = score,
            Evidence = $"actual={actual} {unit}; limit={limit} {unit}",
        });
    }

    private static BenchmarkEvaluationCheckDto FailedArtifactCheck(string path, string evidence)
        => new()
        {
            Id = $"artifact:{path}",
            Category = "instruction",
            Passed = false,
            Score = 0,
            Evidence = evidence,
        };

    private static string? NormalizeRelativePattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return null;

        var normalized = value.Trim().Replace('\\', '/');
        return normalized.Split('/').Any(segment => segment is "" or "." or "..")
            ? null
            : normalized;
    }

    private static Regex BuildGlobRegex(string pattern)
    {
        var expression = "^" + Regex.Escape(pattern)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static BenchmarkSubAgentRoute ParseSubAgentRoute(PuddingPlatform.Data.Entities.SubAgentRunEntity run)
    {
        string? role = null;
        string? profile = null;
        if (!string.IsNullOrWhiteSpace(run.TaskPlanningMetadataJson))
        {
            try
            {
                using var document = JsonDocument.Parse(run.TaskPlanningMetadataJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    role = ReadJsonString(document.RootElement, "role_in_plan")
                        ?? ReadJsonString(document.RootElement, "role");
                    profile = ReadJsonString(document.RootElement, "profile_id")
                        ?? ReadJsonString(document.RootElement, "profileId");
                }
            }
            catch (JsonException)
            {
            }
        }

        return new BenchmarkSubAgentRoute(
            string.IsNullOrWhiteSpace(role) ? run.TemplateId : role,
            profile,
            run.AgentInstanceId);
    }

    private static string? ReadJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record BenchmarkSubAgentRoute(
        string? RoleId,
        string? ProfileId,
        string? AgentInstanceId);
}

public sealed record BenchmarkEvaluationResultDto
{
    public required string RunId { get; init; }
    public required string CaseId { get; init; }
    public string CaseVersion { get; init; } = "1";
    public string? CaseConfigHash { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string Status { get; init; }
    public int? InstructionScore { get; init; }
    public int EfficiencyScore { get; init; }
    public int ReliabilityScore { get; init; }
    public int? OverallScore { get; init; }
    public required BenchmarkObservedMetricsDto Metrics { get; init; }
    public IReadOnlyList<BenchmarkEvaluationCheckDto> Checks { get; init; } = [];
    public required SessionBenchmarkScoresDto DiagnosticScores { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; }
}

public sealed record BenchmarkObservedMetricsDto
{
    public long? DurationMs { get; init; }
    public int LlmCalls { get; init; }
    public int Rounds { get; init; }
    public string? TerminalStatus { get; init; }
    public int ToolCalls { get; init; }
    public int FailedToolResults { get; init; }
    public int BlockingToolFailures { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public long CacheHitTokens { get; init; }
    public long CacheMissTokens { get; init; }
    public double? CacheHitRate { get; init; }
    public decimal CostCny { get; init; }
    public IReadOnlyList<BenchmarkModelUsageDto> ModelUsage { get; init; } = [];
}

public sealed record BenchmarkModelUsageDto
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string Scope { get; init; }
    public string? RoleId { get; init; }
    public string? ProfileId { get; init; }
    public string? AgentInstanceId { get; init; }
    public int Calls { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public long CacheHitTokens { get; init; }
    public decimal CostCny { get; init; }
}

public sealed record BenchmarkEvaluationCheckDto
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public bool Passed { get; init; }
    public int Score { get; init; }
    public required string Evidence { get; init; }
}
