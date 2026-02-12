using System.Text.Json;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// File-backed Hermes benchmark case catalog.
/// The prompt is only returned by detail lookup so the UI list cannot leak task text accidentally.
/// </summary>
public sealed class BenchmarkCaseCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] ForbiddenPromptTerms =
    [
        "基准测试",
        "benchmark",
        "评测",
        "测试你的能力",
        "工具",
        "审批",
        "MCP",
        "mcp",
        "子代理",
        "按系统要求",
    ];

    private readonly PuddingDataPaths _paths;

    public BenchmarkCaseCatalogService(PuddingDataPaths paths)
    {
        _paths = paths;
    }

    private string ConfigPath => Path.Combine(_paths.ConfigRoot, "benchmark-cases", "hermes-agent-cases.json");

    public async Task<IReadOnlyList<BenchmarkCaseSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var cases = await LoadAsync(ct);
        return cases
            .Where(IsVisible)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new BenchmarkCaseSummaryDto
            {
                Id = item.Id,
                Title = item.Title,
                Category = item.Category,
                Coverage = item.Coverage,
                Difficulty = NormalizeDifficulty(item.Difficulty),
                EstimatedRounds = item.EstimatedRounds,
                SeedId = item.SeedId,
                CapabilityTargets = item.CapabilityTargets,
                SortOrder = item.SortOrder,
            })
            .ToList();
    }

    public async Task<BenchmarkCaseDetailDto?> GetAsync(string caseId, CancellationToken ct = default)
    {
        var item = await GetConfigAsync(caseId, ct);
        if (item is null)
            return null;

        return new BenchmarkCaseDetailDto
        {
            Id = item.Id,
            Title = item.Title,
            Prompt = item.Prompt,
        };
    }

    public async Task<BenchmarkCaseConfig?> GetConfigAsync(string caseId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
            return null;

        var cases = await LoadAsync(ct);
        var item = cases.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, caseId, StringComparison.OrdinalIgnoreCase));

        return item is null || !IsVisible(item) ? null : item;
    }

    public async Task<IReadOnlyList<BenchmarkCaseConfig>> LoadAsync(CancellationToken ct = default)
    {
        var defaults = await LoadFileAsync(DefaultConfigPath(), ct);
        var configured = await LoadFileAsync(ConfigPath, ct);
        if (defaults.Count == 0)
            return configured;
        if (configured.Count == 0)
            return defaults;

        var merged = new Dictionary<string, BenchmarkCaseConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in defaults)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                merged[item.Id] = item;
        }

        foreach (var item in configured)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                merged[item.Id] = item;
        }

        return merged.Values
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static string DefaultConfigPath()
        => Path.Combine(AppContext.BaseDirectory, "default-data", "config", "benchmark-cases", "hermes-agent-cases.json");

    private static async Task<IReadOnlyList<BenchmarkCaseConfig>> LoadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<List<BenchmarkCaseConfig>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool IsPromptSafe(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        return !ForbiddenPromptTerms.Any(term =>
            prompt.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVisible(BenchmarkCaseConfig item)
        => item.IsEnabled
            && !string.IsNullOrWhiteSpace(item.Id)
            && !string.IsNullOrWhiteSpace(item.Title)
            && IsPromptSafe(item.Prompt);

    private static string NormalizeDifficulty(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "easy" or "medium" or "hard" or "extreme"
            ? normalized
            : "medium";
    }
}

/// <summary>
/// Compatibility facade kept for existing callers while benchmark services are split by responsibility.
/// New code should depend on BenchmarkCaseCatalogService directly.
/// </summary>
public sealed class BenchmarkCaseFileService
{
    private readonly BenchmarkCaseCatalogService _catalog;

    public BenchmarkCaseFileService(BenchmarkCaseCatalogService catalog)
    {
        _catalog = catalog;
    }

    public Task<IReadOnlyList<BenchmarkCaseSummaryDto>> ListAsync(CancellationToken ct = default)
        => _catalog.ListAsync(ct);

    public Task<BenchmarkCaseDetailDto?> GetAsync(string caseId, CancellationToken ct = default)
        => _catalog.GetAsync(caseId, ct);

    public Task<IReadOnlyList<BenchmarkCaseConfig>> LoadAsync(CancellationToken ct = default)
        => _catalog.LoadAsync(ct);

    public static bool IsPromptSafe(string? prompt) => BenchmarkCaseCatalogService.IsPromptSafe(prompt);
}

public sealed record BenchmarkCaseConfig
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public IReadOnlyList<string> Coverage { get; init; } = [];
    public string Difficulty { get; init; } = "medium";
    public string? EstimatedRounds { get; init; }
    public string? SeedId { get; init; }
    public IReadOnlyList<string> CapabilityTargets { get; init; } = [];
    public required string Prompt { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; } = 100;
}

public sealed record BenchmarkCaseSummaryDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public IReadOnlyList<string> Coverage { get; init; } = [];
    public string Difficulty { get; init; } = "medium";
    public string? EstimatedRounds { get; init; }
    public string? SeedId { get; init; }
    public IReadOnlyList<string> CapabilityTargets { get; init; } = [];
    public int SortOrder { get; init; }
}

public sealed record BenchmarkCaseDetailDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Prompt { get; init; }
}
