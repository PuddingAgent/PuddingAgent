using System.Text.Json;
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
            CaseTitle = benchmarkCase.Title,
            Difficulty = benchmarkCase.Difficulty,
            EstimatedRounds = benchmarkCase.EstimatedRounds,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            SeedId = seed.SeedId,
            SeedFiles = seed.Files,
            CreatedAtUtc = _timeProvider.GetUtcNow(),
        };

        var root = Path.Combine(_paths.RuntimeRoot, "benchmark-runs");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{run.RunId}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(run, JsonOptions), ct);
        return run;
    }
}

public sealed record BenchmarkRunDto
{
    public required string RunId { get; init; }
    public required string CaseId { get; init; }
    public required string CaseTitle { get; init; }
    public string Difficulty { get; init; } = "medium";
    public string? EstimatedRounds { get; init; }
    public required string WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? SeedId { get; init; }
    public IReadOnlyList<BenchmarkSeedFileDto> SeedFiles { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; }
}
