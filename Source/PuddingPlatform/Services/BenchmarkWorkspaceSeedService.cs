using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// Prepares benchmark seed files inside a workspace before the task prompt is sent.
/// Seed data is copied from data/benchmark-seeds/{seedId} into data/workspaces/{workspaceId}.
/// </summary>
public sealed class BenchmarkWorkspaceSeedService
{
    private readonly PuddingDataPaths _paths;

    public BenchmarkWorkspaceSeedService(PuddingDataPaths paths)
    {
        _paths = paths;
    }

    public Task<BenchmarkSeedResultDto> PrepareAsync(
        BenchmarkCaseConfig benchmarkCase,
        string workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("Workspace id cannot be empty.", nameof(workspaceId));

        if (string.IsNullOrWhiteSpace(benchmarkCase.SeedId))
        {
            return Task.FromResult(new BenchmarkSeedResultDto
            {
                SeedId = null,
                Files = [],
            });
        }

        var safeWorkspaceId = SanitizeSegment(workspaceId);
        var safeSeedId = SanitizeSegment(benchmarkCase.SeedId);
        var sourceRoot = Path.Combine(_paths.DataRoot, "benchmark-seeds", safeSeedId);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Benchmark seed '{safeSeedId}' was not found.");

        var workspaceRoot = _paths.WorkspaceRoot(safeWorkspaceId);
        Directory.CreateDirectory(workspaceRoot);

        var copiedFiles = new List<BenchmarkSeedFileDto>();
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.GetFullPath(Path.Combine(workspaceRoot, relative));
            if (!IsInsideDirectory(targetPath, workspaceRoot))
                throw new InvalidOperationException($"Seed file path escapes workspace: {relative}");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            copiedFiles.Add(new BenchmarkSeedFileDto
            {
                Path = relative.Replace('\\', '/'),
                Bytes = new FileInfo(targetPath).Length,
            });
        }

        return Task.FromResult(new BenchmarkSeedResultDto
        {
            SeedId = safeSeedId,
            Files = copiedFiles,
        });
    }

    private static string SanitizeSegment(string value)
    {
        var sanitized = new string(value.Trim()
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            .ToArray());
        if (string.IsNullOrWhiteSpace(sanitized)
            || sanitized is "." or ".."
            || sanitized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid path segment.");
        }

        return sanitized;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record BenchmarkSeedResultDto
{
    public string? SeedId { get; init; }
    public IReadOnlyList<BenchmarkSeedFileDto> Files { get; init; } = [];
}

public sealed record BenchmarkSeedFileDto
{
    public required string Path { get; init; }
    public long Bytes { get; init; }
}
