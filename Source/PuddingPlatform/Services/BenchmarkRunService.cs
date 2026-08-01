using System.Text.Json;
using System.Security.Cryptography;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// Persists benchmark run metadata so diagnostics can correlate a session with the selected case and seed data.
/// </summary>
public sealed class BenchmarkRunService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PuddingDataPaths _paths;
    private readonly TimeProvider _timeProvider;

    public BenchmarkRunService(PuddingDataPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BenchmarkRunDto> CreateAsync(
        BenchmarkCaseConfig benchmarkCase,
        string workspaceId,
        string? sessionId,
        BenchmarkSeedResultDto seed,
        CancellationToken ct = default)
    {
        var run = new BenchmarkRunDto
        {
            RunId = "brun_" + Guid.NewGuid().ToString("N"),
            CaseId = benchmarkCase.Id,
            CaseVersion = benchmarkCase.Version,
            CaseConfigHash = ComputeConfigHash(benchmarkCase),
            CaseTitle = benchmarkCase.Title,
            Difficulty = benchmarkCase.Difficulty,
            EstimatedRounds = benchmarkCase.EstimatedRounds,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            SeedId = seed.SeedId,
            SeedFiles = seed.Files,
            Evaluation = benchmarkCase.Evaluation,
            CreatedAtUtc = _timeProvider.GetUtcNow(),
        };

        var root = Path.Combine(_paths.RuntimeRoot, "benchmark-runs");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{run.RunId}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(run, JsonOptions), ct);
        return run;
    }

    public async Task<BenchmarkRunDto?> GetAsync(string runId, CancellationToken ct = default)
    {
        var path = ResolveRunPath(runId);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<BenchmarkRunDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<BenchmarkEvaluationResultDto?> GetEvaluationAsync(
        string runId,
        CancellationToken ct = default)
    {
        var path = ResolveEvaluationPath(runId);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<BenchmarkEvaluationResultDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveEvaluationAsync(
        BenchmarkEvaluationResultDto result,
        CancellationToken ct = default)
    {
        var path = ResolveEvaluationPath(result.RunId)
            ?? throw new ArgumentException("Invalid benchmark run id.", nameof(result));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(result, JsonOptions), ct);
        File.Move(tempPath, path, overwrite: true);
    }

    private string? ResolveRunPath(string runId)
        => IsSafeRunId(runId)
            ? Path.Combine(_paths.RuntimeRoot, "benchmark-runs", $"{runId}.json")
            : null;

    private string? ResolveEvaluationPath(string runId)
        => IsSafeRunId(runId)
            ? Path.Combine(_paths.RuntimeRoot, "benchmark-runs", $"{runId}.evaluation.json")
            : null;

    private static bool IsSafeRunId(string? runId)
        => !string.IsNullOrWhiteSpace(runId)
            && runId.StartsWith("brun_", StringComparison.Ordinal)
            && runId.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static string ComputeConfigHash(BenchmarkCaseConfig benchmarkCase)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(benchmarkCase, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed record BenchmarkRunDto
{
    public required string RunId { get; init; }
    public required string CaseId { get; init; }
    public string CaseVersion { get; init; } = "1";
    public string? CaseConfigHash { get; init; }
    public required string CaseTitle { get; init; }
    public string Difficulty { get; init; } = "medium";
    public string? EstimatedRounds { get; init; }
    public required string WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? SeedId { get; init; }
    public IReadOnlyList<BenchmarkSeedFileDto> SeedFiles { get; init; } = [];
    public BenchmarkEvaluationContract? Evaluation { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}
