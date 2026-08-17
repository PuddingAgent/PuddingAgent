using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Runtime;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Quarantines legacy data/agents/{subSessionId} directories created as a side effect of
/// reading an absent private-SKILL index. Only the exact empty generated scaffold is eligible;
/// directories containing a manifest, goal, heartbeat, memory, real SKILL, or unknown content
/// are never changed by this service.
/// </summary>
public sealed partial class SubAgentTransientDirectoryGcService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> TerminalRunStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "budget_exhausted",
        "failed",
        "cancelled",
        "timed_out",
        "interrupted",
    };

    private readonly PuddingDataPaths _paths;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ISubAgentPool _subAgentPool;
    private readonly IRuntimeExecutionConfigService _executionConfig;
    private readonly ILogger<SubAgentTransientDirectoryGcService> _logger;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    public SubAgentTransientDirectoryGcService(
        PuddingDataPaths paths,
        IDbContextFactory<PlatformDbContext> dbFactory,
        ISubAgentPool subAgentPool,
        IRuntimeExecutionConfigService executionConfig,
        ILogger<SubAgentTransientDirectoryGcService> logger)
    {
        _paths = paths;
        _dbFactory = dbFactory;
        _subAgentPool = subAgentPool;
        _executionConfig = executionConfig;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var options = GetOptions();
        if (!options.Enabled)
        {
            _logger.LogInformation("[SubAgentDirectoryGc] Disabled by runtime.execution.json");
            return;
        }

        try
        {
            // Let interrupted-run recovery finish before the first retention scan.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SubAgentDirectoryGc] Sweep failed");
            }

            options = GetOptions();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(options.ScanIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<SubAgentTransientDirectoryGcResult> SweepOnceAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await _sweepGate.WaitAsync(ct);
        try
        {
            var options = GetOptions();
            if (!options.Enabled)
                return new SubAgentTransientDirectoryGcResult();

            var purged = await PurgeExpiredQuarantineAsync(nowUtc, options, ct);
            if (!Directory.Exists(_paths.AgentInstancesRoot))
                return new SubAgentTransientDirectoryGcResult { Purged = purged };

            var scanLimit = Math.Min(5000, options.MaxItemsPerSweep * 4);
            var candidates = Directory
                .EnumerateDirectories(_paths.AgentInstancesRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => SubSessionDirectoryNameRegex().IsMatch(directory.Name))
                .OrderBy(directory => directory.LastWriteTimeUtc)
                .Take(scanLimit)
                .ToArray();

            HashSet<string> pooledSubSessionIds;
            try
            {
                pooledSubSessionIds = _subAgentPool.List()
                    .Select(entry => entry.SubSessionId)
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SubAgentDirectoryGc] Pool-state lookup failed; no source directories changed");
                return new SubAgentTransientDirectoryGcResult
                {
                    Scanned = candidates.Length,
                    Purged = purged,
                    Errors = 1,
                };
            }

            IReadOnlyDictionary<string, RunIndexSnapshot> latestRuns;
            try
            {
                latestRuns = await LoadLatestRunsAsync(candidates.Select(x => x.Name), ct);
            }
            catch (Exception ex)
            {
                // DB/file run state is the safety fence. A failed query must never degrade to deletion.
                _logger.LogError(ex, "[SubAgentDirectoryGc] Run-state lookup failed; no directories changed");
                return new SubAgentTransientDirectoryGcResult
                {
                    Scanned = candidates.Length,
                    Purged = purged,
                    Errors = 1,
                };
            }

            var scanned = 0;
            var quarantined = 0;
            var skippedRecent = 0;
            var skippedPooled = 0;
            var skippedNonTerminal = 0;
            var skippedStatefulOrUnknown = 0;
            var errors = 0;

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (quarantined >= options.MaxItemsPerSweep)
                    break;

                scanned++;
                if (pooledSubSessionIds.Contains(candidate.Name))
                {
                    skippedPooled++;
                    continue;
                }

                if (!await IsEmptyGeneratedSkillScaffoldAsync(candidate, ct))
                {
                    skippedStatefulOrUnknown++;
                    continue;
                }

                latestRuns.TryGetValue(candidate.Name, out var latestRun);
                if (latestRun is not null && !TerminalRunStatuses.Contains(latestRun.Status))
                {
                    skippedNonTerminal++;
                    continue;
                }

                var lastRelevantAt = latestRun is null
                    ? new DateTimeOffset(candidate.LastWriteTimeUtc)
                    : ParseTimestamp(latestRun.CompletedAt)
                      ?? ParseTimestamp(latestRun.StartedAt)
                      ?? new DateTimeOffset(candidate.LastWriteTimeUtc);
                var retentionHours = latestRun is null
                    ? options.OrphanRetentionHours
                    : options.ScaffoldRetentionHours;
                if (lastRelevantAt > nowUtc.AddHours(-retentionHours))
                {
                    skippedRecent++;
                    continue;
                }

                try
                {
                    if (await QuarantineAsync(candidate, latestRun, nowUtc, ct))
                        quarantined++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    errors++;
                    _logger.LogWarning(
                        ex,
                        "[SubAgentDirectoryGc] Failed to quarantine directory={Directory}",
                        candidate.FullName);
                }
            }

            var result = new SubAgentTransientDirectoryGcResult
            {
                Scanned = scanned,
                Quarantined = quarantined,
                Purged = purged,
                SkippedRecent = skippedRecent,
                SkippedPooled = skippedPooled,
                SkippedNonTerminal = skippedNonTerminal,
                SkippedStatefulOrUnknown = skippedStatefulOrUnknown,
                Errors = errors,
            };
            _logger.LogInformation(
                "[SubAgentDirectoryGc] scanned={Scanned} quarantined={Quarantined} purged={Purged} " +
                "recent={Recent} pooled={Pooled} nonTerminal={NonTerminal} statefulOrUnknown={Stateful} errors={Errors}",
                result.Scanned,
                result.Quarantined,
                result.Purged,
                result.SkippedRecent,
                result.SkippedPooled,
                result.SkippedNonTerminal,
                result.SkippedStatefulOrUnknown,
                result.Errors);
            return result;
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private SubAgentTransientDirectoryRetentionOptions GetOptions() =>
        _executionConfig.GetOptions().SubAgents.TransientDirectoryRetention;

    private async Task<IReadOnlyDictionary<string, RunIndexSnapshot>> LoadLatestRunsAsync(
        IEnumerable<string> subSessionIds,
        CancellationToken ct)
    {
        var ids = subSessionIds.Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, RunIndexSnapshot>(StringComparer.Ordinal);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        foreach (var batch in ids.Chunk(200))
        {
            var rows = await db.SubAgentRuns
                .AsNoTracking()
                .Where(run => batch.Contains(run.SubSessionId))
                .Select(run => new
                {
                    run.SubSessionId,
                    run.RunId,
                    run.Status,
                    run.StartedAt,
                    run.CompletedAt,
                })
                .ToListAsync(ct);

            foreach (var group in rows.GroupBy(row => row.SubSessionId, StringComparer.Ordinal))
            {
                var latest = group
                    .OrderByDescending(row => row.StartedAt, StringComparer.Ordinal)
                    .First();
                result[group.Key] = new RunIndexSnapshot(
                    latest.RunId,
                    latest.Status,
                    latest.StartedAt,
                    latest.CompletedAt);
            }
        }

        return result;
    }

    private static async Task<bool> IsEmptyGeneratedSkillScaffoldAsync(
        DirectoryInfo directory,
        CancellationToken ct)
    {
        directory.Refresh();
        if (!directory.Exists || IsReparsePoint(directory))
            return false;
        if (File.Exists(Path.Combine(directory.FullName, "manifest.json")))
            return false;
        if (directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Any())
            return false;

        var childDirectories = directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToArray();
        if (childDirectories.Length != 1
            || !string.Equals(childDirectories[0].Name, "skills", StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(childDirectories[0]))
        {
            return false;
        }

        var skillsDirectory = childDirectories[0];
        if (skillsDirectory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).Any())
            return false;
        var files = skillsDirectory.EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToArray();
        if (files.Length != 1
            || !string.Equals(files[0].Name, "index.json", StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(files[0]))
        {
            return false;
        }

        await using var stream = files[0].OpenRead();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("agentInstanceId", out var agentId)
            || !string.Equals(agentId.GetString(), directory.Name, StringComparison.Ordinal)
            || !root.TryGetProperty("skills", out var skills)
            || skills.ValueKind != JsonValueKind.Array
            || skills.GetArrayLength() != 0)
        {
            return false;
        }

        return true;
    }

    private async Task<bool> QuarantineAsync(
        DirectoryInfo source,
        RunIndexSnapshot? latestRun,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var agentsRoot = EnsureTrailingSeparator(Path.GetFullPath(_paths.AgentInstancesRoot));
        var sourcePath = Path.GetFullPath(source.FullName);
        if (!sourcePath.StartsWith(agentsRoot, PathComparison)
            || !string.Equals(Path.GetDirectoryName(sourcePath), agentsRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
        {
            return false;
        }

        Directory.CreateDirectory(_paths.SubAgentTransientDirectoryQuarantineRoot);
        var targetPath = Path.Combine(
            _paths.SubAgentTransientDirectoryQuarantineRoot,
            $"{nowUtc:yyyyMMddTHHmmssfffZ}--{Guid.NewGuid():N}");

        Directory.Move(sourcePath, targetPath);
        try
        {
            var metadata = new QuarantineMetadata
            {
                SubSessionId = source.Name,
                OriginalPath = sourcePath,
                QuarantinedAtUtc = nowUtc,
                LatestRunId = latestRun?.RunId,
                LatestRunStatus = latestRun?.Status,
                Reason = latestRun is null
                    ? "orphan_empty_generated_skill_scaffold"
                    : "terminal_empty_generated_skill_scaffold",
            };
            var json = JsonSerializer.Serialize(metadata, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "gc.json"), json, ct);
            return true;
        }
        catch
        {
            // Metadata is required for later purge. Restore the source if recording it fails.
            if (!Directory.Exists(sourcePath) && Directory.Exists(targetPath))
                Directory.Move(targetPath, sourcePath);
            throw;
        }
    }

    private async Task<int> PurgeExpiredQuarantineAsync(
        DateTimeOffset nowUtc,
        SubAgentTransientDirectoryRetentionOptions options,
        CancellationToken ct)
    {
        var root = _paths.SubAgentTransientDirectoryQuarantineRoot;
        if (!Directory.Exists(root))
            return 0;

        var purged = 0;
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (purged >= options.MaxItemsPerSweep)
                break;

            var directory = new DirectoryInfo(path);
            if (!await IsExpectedQuarantineDirectoryAsync(directory, nowUtc, options, ct))
                continue;

            var skillsRoot = Path.Combine(path, "skills");
            File.Delete(Path.Combine(skillsRoot, "index.json"));
            Directory.Delete(skillsRoot, recursive: false);
            File.Delete(Path.Combine(path, "gc.json"));
            Directory.Delete(path, recursive: false);
            purged++;
        }

        return purged;
    }

    private static async Task<bool> IsExpectedQuarantineDirectoryAsync(
        DirectoryInfo directory,
        DateTimeOffset nowUtc,
        SubAgentTransientDirectoryRetentionOptions options,
        CancellationToken ct)
    {
        if (!directory.Exists || IsReparsePoint(directory))
            return false;

        var rootFiles = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToArray();
        var childDirectories = directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToArray();
        if (rootFiles.Length != 1
            || !string.Equals(rootFiles[0].Name, "gc.json", StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(rootFiles[0])
            || childDirectories.Length != 1
            || !string.Equals(childDirectories[0].Name, "skills", StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(childDirectories[0]))
        {
            return false;
        }

        var skillsDirectory = childDirectories[0];
        if (skillsDirectory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).Any())
            return false;
        var skillFiles = skillsDirectory.EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToArray();
        if (skillFiles.Length != 1
            || !string.Equals(skillFiles[0].Name, "index.json", StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(skillFiles[0]))
        {
            return false;
        }

        await using var stream = rootFiles[0].OpenRead();
        var metadata = await JsonSerializer.DeserializeAsync<QuarantineMetadata>(stream, JsonOptions, ct);
        return metadata is not null
               && !string.IsNullOrWhiteSpace(metadata.SubSessionId)
               && nowUtc >= metadata.QuarantinedAtUtc.AddDays(options.QuarantineRetentionDays);
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static string EnsureTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record RunIndexSnapshot(
        string RunId,
        string Status,
        string StartedAt,
        string? CompletedAt);

    private sealed record QuarantineMetadata
    {
        public required string SubSessionId { get; init; }
        public required string OriginalPath { get; init; }
        public required DateTimeOffset QuarantinedAtUtc { get; init; }
        public string? LatestRunId { get; init; }
        public string? LatestRunStatus { get; init; }
        public required string Reason { get; init; }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+-sub-[0-9a-fA-F]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SubSessionDirectoryNameRegex();
}

public sealed record SubAgentTransientDirectoryGcResult
{
    public int Scanned { get; init; }
    public int Quarantined { get; init; }
    public int Purged { get; init; }
    public int SkippedRecent { get; init; }
    public int SkippedPooled { get; init; }
    public int SkippedNonTerminal { get; init; }
    public int SkippedStatefulOrUnknown { get; init; }
    public int Errors { get; init; }
}
