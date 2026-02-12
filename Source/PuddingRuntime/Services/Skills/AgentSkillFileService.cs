using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Filesystem-backed SKILL service for runtime-private Agent instance skills.
/// </summary>
public sealed partial class AgentSkillFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PuddingDataPaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AgentSkillFileService()
        : this(ResolveDefaultDataPaths())
    {
    }

    public AgentSkillFileService(PuddingDataPaths paths)
    {
        _paths = paths;
    }

    /// <summary>Ensures the runtime Agent SKILL root and index file exist.</summary>
    public async Task<AgentSkillInitializeResult> InitializeAsync(
        string agentInstanceId,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);

        await _writeLock.WaitAsync(ct);
        try
        {
            var skillsRoot = GetSkillsRoot(agentInstanceId);
            Directory.CreateDirectory(skillsRoot);

            var indexPath = GetIndexPath(agentInstanceId);
            if (!File.Exists(indexPath))
            {
                await WriteIndexAsync(
                    indexPath,
                    new AgentSkillIndex
                    {
                        AgentInstanceId = agentInstanceId,
                        GeneratedAt = DateTimeOffset.UtcNow,
                        Skills = [],
                    },
                    ct);
            }

            return new AgentSkillInitializeResult(skillsRoot, indexPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Creates a new SKILL directory, manifest, markdown entry file, and refreshed index.</summary>
    public async Task<AgentSkillRecord> CreateAsync(
        string agentInstanceId,
        AgentSkillCreateRequest request,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        ValidateSkillId(request.SkillId);

        await _writeLock.WaitAsync(ct);
        try
        {
            var skillsRoot = GetSkillsRoot(agentInstanceId);
            Directory.CreateDirectory(skillsRoot);

            var skillRoot = ResolveSkillRoot(agentInstanceId, request.SkillId);
            if (Directory.Exists(skillRoot))
                throw new InvalidOperationException($"SKILL '{request.SkillId}' already exists.");

            Directory.CreateDirectory(skillRoot);

            var now = DateTimeOffset.UtcNow;
            var markdown = request.SkillMarkdown ?? string.Empty;
            var summary = string.IsNullOrWhiteSpace(request.Summary)
                ? DeriveSummary(markdown)
                : request.Summary.Trim();

            var manifest = new AgentSkillManifest
            {
                SkillId = request.SkillId,
                Name = string.IsNullOrWhiteSpace(request.Name) ? request.SkillId : request.Name.Trim(),
                Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim(),
                Description = NormalizeOptional(request.Description),
                Summary = summary,
                Tags = NormalizeTags(request.Tags),
                Keywords = NormalizeTags(request.Keywords),
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            manifest = manifest with { ContentHash = ComputeContentHash(manifest, markdown) };

            await AtomicFileWriter.WriteJsonAsync(GetManifestPath(skillRoot), manifest, JsonOptions, ct);
            await AtomicFileWriter.WriteAsync(GetSkillMarkdownPath(skillRoot), markdown, ct);
            var index = await RebuildIndexCoreAsync(agentInstanceId, ct);

            return new AgentSkillRecord(manifest, skillRoot, index);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads a SKILL manifest and its physical directory path.</summary>
    public async Task<AgentSkillRecord> GetAsync(
        string agentInstanceId,
        string skillId,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        ValidateSkillId(skillId);

        var skillRoot = ResolveSkillRoot(agentInstanceId, skillId);
        var manifestPath = GetManifestPath(skillRoot);
        if (!File.Exists(manifestPath))
            throw new DirectoryNotFoundException($"SKILL '{skillId}' was not found.");

        var manifest = await AtomicFileWriter.ReadJsonAsync<AgentSkillManifest>(manifestPath, JsonOptions, ct)
            ?? throw new InvalidOperationException($"SKILL '{skillId}' manifest is empty.");
        var index = await GetIndexAsync(agentInstanceId, ct);
        return new AgentSkillRecord(manifest, skillRoot, index);
    }

    /// <summary>Lists all SKILL records for an Agent instance in deterministic order.</summary>
    public async Task<IReadOnlyList<AgentSkillRecord>> ListAsync(
        string agentInstanceId,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        await InitializeAsync(agentInstanceId, ct);

        var records = new List<AgentSkillRecord>();
        var skillsRoot = GetSkillsRoot(agentInstanceId);
        var index = await GetIndexAsync(agentInstanceId, ct);
        foreach (var dir in Directory.EnumerateDirectories(skillsRoot))
        {
            var manifestPath = GetManifestPath(dir);
            if (!File.Exists(manifestPath))
                continue;

            var manifest = await AtomicFileWriter.ReadJsonAsync<AgentSkillManifest>(manifestPath, JsonOptions, ct);
            if (manifest is null)
                continue;

            records.Add(new AgentSkillRecord(manifest, dir, index));
        }

        return records
            .OrderBy(x => x.Manifest.SkillId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Manifest.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Updates a SKILL manifest and optionally its SKILL.md content.</summary>
    public async Task<AgentSkillRecord> UpdateAsync(
        string agentInstanceId,
        string skillId,
        AgentSkillUpdateRequest request,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        ValidateSkillId(skillId);

        await _writeLock.WaitAsync(ct);
        try
        {
            var skillRoot = ResolveSkillRoot(agentInstanceId, skillId);
            var manifest = await ReadRequiredManifestAsync(skillRoot, skillId, ct);
            var markdownPath = GetSkillMarkdownPath(skillRoot);
            var markdown = request.SkillMarkdown is null
                ? File.Exists(markdownPath) ? await File.ReadAllTextAsync(markdownPath, ct) : string.Empty
                : request.SkillMarkdown;

            var updated = manifest with
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? manifest.Name : request.Name.Trim(),
                Version = string.IsNullOrWhiteSpace(request.Version) ? manifest.Version : request.Version.Trim(),
                Description = request.Description is null ? manifest.Description : NormalizeOptional(request.Description),
                Summary = request.Summary is null
                    ? manifest.Summary
                    : string.IsNullOrWhiteSpace(request.Summary)
                        ? DeriveSummary(markdown)
                        : request.Summary.Trim(),
                Tags = request.Tags is null ? manifest.Tags : NormalizeTags(request.Tags),
                Keywords = request.Keywords is null ? manifest.Keywords : NormalizeTags(request.Keywords),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            updated = updated with { ContentHash = ComputeContentHash(updated, markdown) };

            await AtomicFileWriter.WriteJsonAsync(GetManifestPath(skillRoot), updated, JsonOptions, ct);
            if (request.SkillMarkdown is not null)
                await AtomicFileWriter.WriteAsync(markdownPath, markdown, ct);
            var index = await RebuildIndexCoreAsync(agentInstanceId, ct);
            return new AgentSkillRecord(updated, skillRoot, index);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Changes whether a SKILL is visible in the Agent's index.</summary>
    public async Task<AgentSkillRecord> SetEnabledAsync(
        string agentInstanceId,
        string skillId,
        bool enabled,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        ValidateSkillId(skillId);

        await _writeLock.WaitAsync(ct);
        try
        {
            var skillRoot = ResolveSkillRoot(agentInstanceId, skillId);
            var manifest = await ReadRequiredManifestAsync(skillRoot, skillId, ct);
            var markdownPath = GetSkillMarkdownPath(skillRoot);
            var markdown = File.Exists(markdownPath)
                ? await File.ReadAllTextAsync(markdownPath, ct)
                : string.Empty;

            var updated = manifest with
            {
                Enabled = enabled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            updated = updated with { ContentHash = ComputeContentHash(updated, markdown) };

            await AtomicFileWriter.WriteJsonAsync(GetManifestPath(skillRoot), updated, JsonOptions, ct);
            var index = await RebuildIndexCoreAsync(agentInstanceId, ct);
            return new AgentSkillRecord(updated, skillRoot, index);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Deletes a SKILL directory and refreshes the index.</summary>
    public async Task<AgentSkillDeleteResult> DeleteAsync(
        string agentInstanceId,
        string skillId,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        ValidateSkillId(skillId);

        await _writeLock.WaitAsync(ct);
        try
        {
            var skillRoot = ResolveSkillRoot(agentInstanceId, skillId);
            if (!Directory.Exists(skillRoot))
                throw new DirectoryNotFoundException($"SKILL '{skillId}' was not found.");

            Directory.Delete(skillRoot, recursive: true);
            var index = await RebuildIndexCoreAsync(agentInstanceId, ct);
            return new AgentSkillDeleteResult(skillId, skillRoot, index);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads the agent SKILL index, initializing it if needed.</summary>
    public async Task<AgentSkillIndex> GetIndexAsync(
        string agentInstanceId,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        await InitializeAsync(agentInstanceId, ct);

        var indexPath = GetIndexPath(agentInstanceId);
        return await AtomicFileWriter.ReadJsonAsync<AgentSkillIndex>(indexPath, JsonOptions, ct)
            ?? new AgentSkillIndex
            {
                AgentInstanceId = agentInstanceId,
                GeneratedAt = DateTimeOffset.UtcNow,
                Skills = [],
            };
    }

    /// <summary>Rebuilds the SKILL index from manifests on disk.</summary>
    public async Task<AgentSkillIndex> RebuildIndexAsync(
        string agentInstanceId,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);

        await _writeLock.WaitAsync(ct);
        try
        {
            return await RebuildIndexCoreAsync(agentInstanceId, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads a file inside a SKILL directory. Defaults to SKILL.md.</summary>
    public async Task<AgentSkillFileContent> ReadFileAsync(
        string agentInstanceId,
        string skillId,
        string? relativePath = null,
        CancellationToken ct = default)
    {
        ValidateAgentInstanceId(agentInstanceId);
        ValidateSkillId(skillId);

        var fileRelativePath = string.IsNullOrWhiteSpace(relativePath)
            ? "SKILL.md"
            : relativePath.Trim();
        ValidateRelativePath(fileRelativePath);

        var skillRoot = ResolveSkillRoot(agentInstanceId, skillId);
        var filePath = ResolveUnderRoot(skillRoot, fileRelativePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"SKILL file '{fileRelativePath}' was not found.", filePath);

        var content = await File.ReadAllTextAsync(filePath, ct);
        return new AgentSkillFileContent(skillId, fileRelativePath, filePath, content);
    }

    private async Task<AgentSkillIndex> RebuildIndexCoreAsync(
        string agentInstanceId,
        CancellationToken ct)
    {
        var skillsRoot = GetSkillsRoot(agentInstanceId);
        Directory.CreateDirectory(skillsRoot);

        var entries = new List<AgentSkillIndexEntry>();
        foreach (var dir in Directory.EnumerateDirectories(skillsRoot))
        {
            var manifestPath = GetManifestPath(dir);
            if (!File.Exists(manifestPath))
                continue;

            var manifest = await AtomicFileWriter.ReadJsonAsync<AgentSkillManifest>(manifestPath, JsonOptions, ct);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.SkillId))
                continue;

            var relativePath = Path.GetRelativePath(_paths.AgentInstanceRoot(agentInstanceId), dir);
            entries.Add(new AgentSkillIndexEntry
            {
                SkillId = manifest.SkillId,
                Name = manifest.Name,
                Version = manifest.Version,
                Description = manifest.Description,
                Summary = manifest.Summary,
                Tags = manifest.Tags,
                Keywords = manifest.Keywords,
                Enabled = manifest.Enabled,
                RelativePath = NormalizeRelativeForJson(relativePath),
                PhysicalPath = dir,
                ContentHash = manifest.ContentHash,
                UpdatedAt = manifest.UpdatedAt,
            });
        }

        var index = new AgentSkillIndex
        {
            AgentInstanceId = agentInstanceId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Skills = entries
                .OrderBy(x => x.SkillId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        await WriteIndexAsync(GetIndexPath(agentInstanceId), index, ct);
        return index;
    }

    private static async Task<AgentSkillManifest> ReadRequiredManifestAsync(
        string skillRoot,
        string skillId,
        CancellationToken ct)
    {
        var manifestPath = GetManifestPath(skillRoot);
        if (!File.Exists(manifestPath))
            throw new DirectoryNotFoundException($"SKILL '{skillId}' was not found.");

        return await AtomicFileWriter.ReadJsonAsync<AgentSkillManifest>(manifestPath, JsonOptions, ct)
            ?? throw new InvalidOperationException($"SKILL '{skillId}' manifest is empty.");
    }

    private static Task WriteIndexAsync(string indexPath, AgentSkillIndex index, CancellationToken ct) =>
        AtomicFileWriter.WriteJsonAsync(indexPath, index, JsonOptions, ct);

    private string GetSkillsRoot(string agentInstanceId)
    {
        var root = Path.Combine(_paths.AgentInstanceRoot(agentInstanceId), "skills");
        return ResolveUnderRoot(_paths.AgentInstanceRoot(agentInstanceId), "skills", rootMustExist: false);
    }

    private string GetIndexPath(string agentInstanceId) =>
        Path.Combine(GetSkillsRoot(agentInstanceId), "index.json");

    private string ResolveSkillRoot(string agentInstanceId, string skillId) =>
        ResolveUnderRoot(GetSkillsRoot(agentInstanceId), skillId, rootMustExist: false);

    private static string GetManifestPath(string skillRoot) =>
        Path.Combine(skillRoot, "manifest.json");

    private static string GetSkillMarkdownPath(string skillRoot) =>
        Path.Combine(skillRoot, "SKILL.md");

    private static string ResolveUnderRoot(string root, string relativePath, bool rootMustExist = true)
    {
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Relative path must not be rooted.", nameof(relativePath));

        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rootMustExist && !Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(fullRoot);

        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!IsUnderRoot(fullRoot, fullPath))
            throw new ArgumentException("Path escapes the Agent SKILL directory.", nameof(relativePath));

        return fullPath;
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.Equals(root, comparison)
               || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
               || path.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static void ValidateAgentInstanceId(string agentInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId) || !AgentInstanceIdRegex().IsMatch(agentInstanceId))
            throw new ArgumentException("Agent instance id contains invalid characters.", nameof(agentInstanceId));
    }

    private static void ValidateSkillId(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId) || !SkillIdRegex().IsMatch(skillId))
            throw new ArgumentException("SKILL id contains invalid characters.", nameof(skillId));
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part == ".."))
        {
            throw new ArgumentException("SKILL file path is invalid.", nameof(relativePath));
        }
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        tags is null
            ? []
            : tags
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DeriveSummary(string markdown)
    {
        foreach (var rawLine in markdown.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line is "---")
                continue;
            if (line.StartsWith('#'))
                line = line.TrimStart('#').Trim();
            if (line.Length == 0)
                continue;
            return line.Length <= 240 ? line : line[..240];
        }

        return string.Empty;
    }

    private static string ComputeContentHash(AgentSkillManifest manifest, string markdown)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            manifest.SkillId,
            manifest.Name,
            manifest.Version,
            manifest.Description,
            manifest.Summary,
            manifest.Tags,
            manifest.Enabled,
            SkillMarkdown = markdown,
        }, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeRelativeForJson(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static PuddingDataPaths ResolveDefaultDataPaths()
    {
        var root = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "data");

        return PuddingDataPaths.FromRoot(root);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex AgentInstanceIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SkillIdRegex();
}

public sealed record AgentSkillCreateRequest
{
    public required string SkillId { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? Description { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Keywords { get; init; }
    public string? SkillMarkdown { get; init; }
}

public sealed record AgentSkillUpdateRequest
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Keywords { get; init; }
    public string? SkillMarkdown { get; init; }
}

public sealed record AgentSkillManifest
{
    public required string SkillId { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? Description { get; init; }
    public string Summary { get; init; } = string.Empty;
        public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string ContentHash { get; init; } = string.Empty;
}

public sealed record AgentSkillIndex
{
    public required string AgentInstanceId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required IReadOnlyList<AgentSkillIndexEntry> Skills { get; init; }
}

public sealed record AgentSkillIndexEntry
{
    public required string SkillId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string Summary { get; init; } = string.Empty;
        public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public bool Enabled { get; init; }
    public required string RelativePath { get; init; }
    public required string PhysicalPath { get; init; }
    public required string ContentHash { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AgentSkillInitializeResult(string SkillsRootPath, string IndexPath);

public sealed record AgentSkillRecord(
    AgentSkillManifest Manifest,
    string PhysicalPath,
    AgentSkillIndex Index);

public sealed record AgentSkillDeleteResult(
    string SkillId,
    string DeletedPath,
    AgentSkillIndex Index);

public sealed record AgentSkillFileContent(
    string SkillId,
    string RelativePath,
    string PhysicalPath,
    string Content);
