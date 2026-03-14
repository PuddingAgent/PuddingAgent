using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingCodeCLI;

public sealed record RuntimeModelRef(string ProviderId, string Model);

public sealed record DualModelRuntimeConfig(
    RuntimeModelRef Conscious,
    RuntimeModelRef Subconscious,
    SubconsciousPolicyConfig Policy);

public sealed record SubconsciousSignal(
    string Recall,
    string Risk,
    string BoundaryCheck,
    string MemoryWriteCandidate);

public sealed record MemoryMaintenanceStatus(
    int ProjectEntries,
    int GlobalEntries,
    int IndexedEntries,
    DateTimeOffset? LastCompactedAt,
    int WritesSinceCompact,
    int CompactWriteThreshold,
    int CompactMinEntries,
    int CompactKeepEntries,
    int RecallWindowSize,
    int RecallHitsInWindow,
    double RecentRecallHitRate,
    int CompactionRuns,
    int LastCompactionSavedEntries,
    double LastCompactionGainRate,
    int TotalCompactionSavedEntries,
    bool UseModelSummarization,
    int ModelSummaryCalls,
    int ModelSummaryDailyTokenBudget,
    int ModelSummaryTokensUsedToday);

public sealed record MemoryMaintenanceOptions(
    int CompactWriteThreshold = 20,
    int CompactMinEntries = 180,
    int CompactKeepEntries = 120,
    bool UseModelSummarization = false,
    int ModelSummaryDailyTokenBudget = 12000,
    int ModelSummaryMaxInputChars = 12000,
    int ModelSummaryMaxOutputChars = 2400,
    Func<string, int, CancellationToken, Task<string?>>? ModelSummarizer = null);

public sealed class SubconsciousEngine
{
    private readonly string _globalMemoryFile;
    private readonly string _projectMemoryFile;
    private readonly string _dailyLogFile;
    private readonly IMemoryIndexer _memoryIndexer;
    private readonly (string Source, string Path)[] _memorySources;
    private readonly string _memoryArchiveDir;
    private readonly string _maintenanceStateFile;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly int _compactWriteThreshold;
    private readonly int _compactMinEntries;
    private readonly int _compactKeepEntries;
    private readonly bool _useModelSummarization;
    private readonly int _modelSummaryDailyTokenBudget;
    private readonly int _modelSummaryMaxInputChars;
    private readonly int _modelSummaryMaxOutputChars;
    private readonly Func<string, int, CancellationToken, Task<string?>>? _modelSummarizer;
    private const int RecallWindowLimit = 50;

    private int _writesSinceCompact;
    private DateTimeOffset? _lastCompactedAt;
    private List<bool> _recentRecallHits = [];
    private int _compactionRuns;
    private int _lastCompactionEntriesBefore;
    private int _lastCompactionEntriesAfter;
    private int _totalCompactionSavedEntries;
    private DateOnly _modelSummaryBudgetDate;
    private int _modelSummaryTokensUsedToday;
    private int _modelSummaryCalls;

    public SubconsciousEngine(string projectRoot, MemoryMaintenanceOptions? options = null)
    {
        options ??= new MemoryMaintenanceOptions();
        _compactWriteThreshold = Math.Max(1, options.CompactWriteThreshold);
        _compactMinEntries = Math.Max(10, options.CompactMinEntries);
        _compactKeepEntries = Math.Clamp(options.CompactKeepEntries, 1, _compactMinEntries);
        _useModelSummarization = options.UseModelSummarization;
        _modelSummaryDailyTokenBudget = Math.Max(256, options.ModelSummaryDailyTokenBudget);
        _modelSummaryMaxInputChars = Math.Max(800, options.ModelSummaryMaxInputChars);
        _modelSummaryMaxOutputChars = Math.Max(200, options.ModelSummaryMaxOutputChars);
        _modelSummarizer = options.ModelSummarizer;
        _modelSummaryBudgetDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _globalMemoryFile = Path.Combine(home, ".pudding", "memory", "global.md");
        _projectMemoryFile = Path.Combine(projectRoot, ".pudding", "memory", "project.md");
        _dailyLogFile = Path.Combine(projectRoot, ".pudding", "logs",
            $"{DateTimeOffset.Now:yyyy-MM-dd}.log");
        _memoryArchiveDir = Path.Combine(projectRoot, ".pudding", "memory", "archive");
        _maintenanceStateFile = Path.Combine(projectRoot, ".pudding", "memory", "maintenance.json");
        var indexPath = Path.Combine(projectRoot, ".pudding", "memory", "index.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_globalMemoryFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_projectMemoryFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_dailyLogFile)!);
        Directory.CreateDirectory(_memoryArchiveDir);

        if (!File.Exists(_globalMemoryFile)) File.WriteAllText(_globalMemoryFile, "# Global Memory\n\n");
        if (!File.Exists(_projectMemoryFile)) File.WriteAllText(_projectMemoryFile, "# Project Memory\n\n");

        _memorySources =
        [
            ("project", _projectMemoryFile),
            ("global", _globalMemoryFile)
        ];
        _memoryIndexer = new LocalMemoryIndexer(indexPath);
        _memoryIndexer.RebuildAsync(_memorySources).GetAwaiter().GetResult();

        LoadMaintenanceState();
    }

    public async Task RecordConsciousAsync(string content, CancellationToken ct = default)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] [C] {content}\n";
        await File.AppendAllTextAsync(_dailyLogFile, line, ct);
    }

    public async Task RecordSubconsciousAsync(string content, CancellationToken ct = default)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] [S] {content}\n";
        await File.AppendAllTextAsync(_dailyLogFile, line, ct);
    }

    public async Task<SubconsciousSignal> GenerateSignalAsync(string userInput, CancellationToken ct = default)
    {
        var recall = await RecallAsync(userInput, ct);
        var risk = AnalyzeRisk(userInput);
        var boundary = AnalyzeBoundary(userInput);
        var memoryCandidate = BuildMemoryCandidate(userInput, recall, risk, boundary);

        return new SubconsciousSignal(recall, risk, boundary, memoryCandidate);
    }

    public async Task PersistMemoryCandidateAsync(string candidate, bool global = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var path = global ? _globalMemoryFile : _projectMemoryFile;
        var line = $"- {DateTimeOffset.Now:yyyy-MM-dd HH:mm} {candidate}\n";
        await File.AppendAllTextAsync(path, line, ct);
        await _memoryIndexer.RebuildAsync(_memorySources, ct);
        _writesSinceCompact++;
        await MaybeCompactMemoriesAsync(force: false, ct);
    }

    public async Task RebuildMemoryIndexAsync(CancellationToken ct = default)
    {
        await _memoryIndexer.RebuildAsync(_memorySources, ct);
    }

    public async Task<MemoryMaintenanceStatus> GetMemoryStatusAsync(CancellationToken ct = default)
    {
        ResetModelSummaryBudgetIfNeeded();
        var projectEntries = await CountMemoryEntriesAsync(_projectMemoryFile, ct);
        var globalEntries = await CountMemoryEntriesAsync(_globalMemoryFile, ct);
        var indexedEntries = await _memoryIndexer.CountAsync(ct);
        var recallWindowSize = _recentRecallHits.Count;
        var recallHitsInWindow = _recentRecallHits.Count(h => h);
        var recentRecallHitRate = recallWindowSize == 0 ? 0 : (double)recallHitsInWindow / recallWindowSize;
        var lastCompactionSaved = Math.Max(0, _lastCompactionEntriesBefore - _lastCompactionEntriesAfter);
        var lastCompactionGainRate = _lastCompactionEntriesBefore <= 0
            ? 0
            : (double)lastCompactionSaved / _lastCompactionEntriesBefore;

        return new MemoryMaintenanceStatus(
            projectEntries,
            globalEntries,
            indexedEntries,
            _lastCompactedAt,
            _writesSinceCompact,
            _compactWriteThreshold,
            _compactMinEntries,
            _compactKeepEntries,
            recallWindowSize,
            recallHitsInWindow,
            recentRecallHitRate,
            _compactionRuns,
            lastCompactionSaved,
            lastCompactionGainRate,
            _totalCompactionSavedEntries,
            _useModelSummarization,
            _modelSummaryCalls,
            _modelSummaryDailyTokenBudget,
            _modelSummaryTokensUsedToday);
    }

    public Task RunMaintenanceAsync(CancellationToken ct = default) =>
        MaybeCompactMemoriesAsync(force: true, ct);

    private async Task MaybeCompactMemoriesAsync(bool force, CancellationToken ct)
    {
        if (!force && _writesSinceCompact < _compactWriteThreshold) return;

        await _maintenanceGate.WaitAsync(ct);
        try
        {
            if (!force && _writesSinceCompact < _compactWriteThreshold) return;

            var projectCompaction = await CompactMemoryFileAsync(_projectMemoryFile, "Project Memory", ct);
            var globalCompaction = await CompactMemoryFileAsync(_globalMemoryFile, "Global Memory", ct);
            var compacted = projectCompaction.Applied || globalCompaction.Applied;
            if (compacted)
            {
                var entriesBefore = projectCompaction.EntriesBefore + globalCompaction.EntriesBefore;
                var entriesAfter = projectCompaction.EntriesAfter + globalCompaction.EntriesAfter;
                var saved = Math.Max(0, entriesBefore - entriesAfter);

                _lastCompactedAt = DateTimeOffset.UtcNow;
                _lastCompactionEntriesBefore = entriesBefore;
                _lastCompactionEntriesAfter = entriesAfter;
                _compactionRuns++;
                _totalCompactionSavedEntries += saved;
                await _memoryIndexer.RebuildAsync(_memorySources, ct);
            }

            _writesSinceCompact = 0;
            await SaveMaintenanceStateAsync(ct);
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    private async Task<CompactionResult> CompactMemoryFileAsync(string path, string title, CancellationToken ct)
    {
        if (!File.Exists(path)) return CompactionResult.None;

        var lines = await File.ReadAllLinesAsync(path, ct);
        var memoryLines = lines
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- "))
            .ToList();

        if (memoryLines.Count < _compactMinEntries)
            return new CompactionResult(false, memoryLines.Count, memoryLines.Count);

        List<string>? distilled = null;
        if (_useModelSummarization && _modelSummarizer is not null)
        {
            distilled = await TryModelDistillAsync(memoryLines, ct);
        }

        distilled ??= DistillEntries(memoryLines, _compactKeepEntries);
        if (distilled.Count == 0)
            return new CompactionResult(false, memoryLines.Count, memoryLines.Count);

        var ts = DateTimeOffset.UtcNow;
        var archiveFile = Path.Combine(
            _memoryArchiveDir,
            $"{Path.GetFileNameWithoutExtension(path)}-{ts:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(archiveFile, string.Join('\n', lines), ct);

        var rebuilt = new StringBuilder();
        rebuilt.AppendLine($"# {title}");
        rebuilt.AppendLine();
        rebuilt.AppendLine($"> compacted_at_utc: {ts:O}");
        rebuilt.AppendLine($"> entries_before: {memoryLines.Count}");
        rebuilt.AppendLine($"> entries_after: {distilled.Count}");
        rebuilt.AppendLine();
        rebuilt.AppendLine("## Distilled");
        rebuilt.AppendLine();
        foreach (var entry in distilled)
            rebuilt.AppendLine($"- {entry}");
        rebuilt.AppendLine();

        await File.WriteAllTextAsync(path, rebuilt.ToString(), ct);
        return new CompactionResult(true, memoryLines.Count, distilled.Count);
    }

    private static List<string> DistillEntries(List<string> memoryLines, int keepCount)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distilled = new List<string>();

        for (var i = memoryLines.Count - 1; i >= 0; i--)
        {
            var normalized = NormalizeMemoryLine(memoryLines[i]);
            if (string.IsNullOrWhiteSpace(normalized)) continue;

            if (seen.Add(normalized))
                distilled.Add(normalized);

            if (distilled.Count >= keepCount)
                break;
        }

        distilled.Reverse();
        return distilled;
    }

    private async Task<List<string>?> TryModelDistillAsync(List<string> memoryLines, CancellationToken ct)
    {
        ResetModelSummaryBudgetIfNeeded();

        var sourceLines = memoryLines
            .Select(NormalizeMemoryLine)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .TakeLast(_compactKeepEntries * 2)
            .ToList();
        if (sourceLines.Count == 0) return null;

        var inputText = string.Join('\n', sourceLines.Select(s => $"- {s}"));
        if (inputText.Length > _modelSummaryMaxInputChars)
            inputText = inputText[.._modelSummaryMaxInputChars];

        var estimatedTokens = EstimateTokens(inputText.Length + _modelSummaryMaxOutputChars);
        if (_modelSummaryTokensUsedToday + estimatedTokens > _modelSummaryDailyTokenBudget)
            return null;

        string? modelOutput;
        try
        {
            var prompt = BuildModelSummaryPrompt(inputText, _compactKeepEntries);
            modelOutput = await _modelSummarizer!.Invoke(prompt, _modelSummaryMaxOutputChars, ct);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(modelOutput))
            return null;

        var distilled = ParseModelSummaryLines(modelOutput, _compactKeepEntries);
        if (distilled.Count == 0) return null;

        _modelSummaryTokensUsedToday += estimatedTokens;
        _modelSummaryCalls++;
        await SaveMaintenanceStateAsync(ct);
        return distilled;
    }

    private static string BuildModelSummaryPrompt(string memoryText, int keepCount)
    {
        return
            "Distill the memory list into concise, non-duplicated bullet points for future coding context.\n" +
            $"Output only plain bullet lines starting with '- '. Keep at most {keepCount} bullets.\n" +
            "Preserve concrete constraints, decisions, and risks. Remove chatter and redundancy.\n\n" +
            "Memory:\n" +
            memoryText;
    }

    private static List<string> ParseModelSummaryLines(string text, int keepCount)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("- "))
                line = line[2..].Trim();
            else if (line.StartsWith("* "))
                line = line[2..].Trim();
            else if (char.IsDigit(line.FirstOrDefault()) && line.Contains('.'))
                line = line[(line.IndexOf('.') + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (seen.Add(line))
                result.Add(line);
            if (result.Count >= keepCount) break;
        }

        return result;
    }

    private void ResetModelSummaryBudgetIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _modelSummaryBudgetDate) return;
        _modelSummaryBudgetDate = today;
        _modelSummaryTokensUsedToday = 0;
    }

    private static int EstimateTokens(int chars)
    {
        return Math.Max(1, chars / 4);
    }

    private static DateOnly ParseBudgetDate(string? value)
    {
        if (DateOnly.TryParse(value, out var date))
            return date;
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static string NormalizeMemoryLine(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("- "))
            text = text[2..].TrimStart();

        text = Regex.Replace(text, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s*", "");
        return text.Trim();
    }

    private static async Task<int> CountMemoryEntriesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return 0;
        var lines = await File.ReadAllLinesAsync(path, ct);
        return lines.Count(l => l.TrimStart().StartsWith("- "));
    }

    private void LoadMaintenanceState()
    {
        if (!File.Exists(_maintenanceStateFile)) return;

        try
        {
            var json = File.ReadAllText(_maintenanceStateFile);
            var state = JsonSerializer.Deserialize<MaintenanceState>(json);
            if (state is null) return;
            _writesSinceCompact = Math.Max(0, state.WritesSinceCompact);
            _lastCompactedAt = state.LastCompactedAt;
            _recentRecallHits = state.RecentRecallHits?
                .Take(RecallWindowLimit)
                .Select(v => v != 0)
                .ToList() ?? [];
            _compactionRuns = Math.Max(0, state.CompactionRuns);
            _lastCompactionEntriesBefore = Math.Max(0, state.LastCompactionEntriesBefore);
            _lastCompactionEntriesAfter = Math.Max(0, state.LastCompactionEntriesAfter);
            _totalCompactionSavedEntries = Math.Max(0, state.TotalCompactionSavedEntries);
            _modelSummaryCalls = Math.Max(0, state.ModelSummaryCalls);
            _modelSummaryTokensUsedToday = Math.Max(0, state.ModelSummaryTokensUsedToday);
            _modelSummaryBudgetDate = ParseBudgetDate(state.ModelSummaryBudgetDate);
        }
        catch
        {
            _writesSinceCompact = 0;
            _lastCompactedAt = null;
            _recentRecallHits = [];
            _compactionRuns = 0;
            _lastCompactionEntriesBefore = 0;
            _lastCompactionEntriesAfter = 0;
            _totalCompactionSavedEntries = 0;
            _modelSummaryCalls = 0;
            _modelSummaryTokensUsedToday = 0;
            _modelSummaryBudgetDate = DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }

    private async Task SaveMaintenanceStateAsync(CancellationToken ct)
    {
        var state = new MaintenanceState
        {
            WritesSinceCompact = _writesSinceCompact,
            LastCompactedAt = _lastCompactedAt,
            RecentRecallHits = _recentRecallHits.Select(v => v ? 1 : 0).ToList(),
            CompactionRuns = _compactionRuns,
            LastCompactionEntriesBefore = _lastCompactionEntriesBefore,
            LastCompactionEntriesAfter = _lastCompactionEntriesAfter,
            TotalCompactionSavedEntries = _totalCompactionSavedEntries,
            ModelSummaryCalls = _modelSummaryCalls,
            ModelSummaryTokensUsedToday = _modelSummaryTokensUsedToday,
            ModelSummaryBudgetDate = _modelSummaryBudgetDate.ToString("yyyy-MM-dd")
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_maintenanceStateFile, json, ct);
    }

    private async Task<string> RecallAsync(string userInput, CancellationToken ct)
    {
        var keywords = ExtractKeywords(userInput);
        var recallItems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (keywords.Count > 0)
        {
            var keywordCandidates = new List<string>();
            keywordCandidates.AddRange(await SearchLinesAsync(_projectMemoryFile, keywords, ct));
            keywordCandidates.AddRange(await SearchLinesAsync(_globalMemoryFile, keywords, ct));

            foreach (var candidate in keywordCandidates)
            {
                if (seen.Add(candidate))
                    recallItems.Add(candidate);
            }
        }

        var vectorCandidates = await _memoryIndexer.SearchAsync(userInput, topK: 3, ct);
        foreach (var candidate in vectorCandidates)
        {
            var text = $"[{candidate.Source}] {candidate.Text}";
            if (seen.Add(text))
                recallItems.Add(text);
        }

        var recallHit = recallItems.Count > 0;
        await TrackRecallAsync(recallHit, ct);
        if (!recallHit) return "No relevant memory found.";
        return string.Join(" | ", recallItems.Take(4));
    }

    private async Task TrackRecallAsync(bool hit, CancellationToken ct)
    {
        _recentRecallHits.Add(hit);
        if (_recentRecallHits.Count > RecallWindowLimit)
            _recentRecallHits.RemoveRange(0, _recentRecallHits.Count - RecallWindowLimit);

        await SaveMaintenanceStateAsync(ct);
    }

    private static string AnalyzeRisk(string input)
    {
        var lowered = input.ToLowerInvariant();
        if (lowered.Contains("delete") || lowered.Contains("rm -rf") || lowered.Contains("drop table"))
            return "High risk operation intent detected. Require explicit confirmation.";
        if (lowered.Contains("refactor") || lowered.Contains("rename"))
            return "Potential medium risk: cross-file impact likely.";
        return "No immediate high-risk signal.";
    }

    private static string AnalyzeBoundary(string input)
    {
        var lowered = input.ToLowerInvariant();
        if (lowered.Contains("outside project") || lowered.Contains("system32") || lowered.Contains("/etc/"))
            return "Boundary warning: possible out-of-project or system path access.";
        return "Boundary check passed.";
    }

    private static string BuildMemoryCandidate(string input, string recall, string risk, string boundary)
    {
        var compactInput = input.Length > 120 ? input[..120] : input;
        return $"Input='{compactInput}'. Recall='{recall}'. Risk='{risk}'. Boundary='{boundary}'.";
    }

    private static HashSet<string> ExtractKeywords(string input)
    {
        var words = input.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '/', '\\', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            if (word.Length < 4) continue;
            set.Add(word);
        }

        return set;
    }

    private static async Task<List<string>> SearchLinesAsync(
        string path,
        HashSet<string> keywords,
        CancellationToken ct)
    {
        var result = new List<string>();
        if (!File.Exists(path)) return result;

        var lines = await File.ReadAllLinesAsync(path, ct);
        foreach (var line in lines)
        {
            if (keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                var normalized = line.Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                    result.Add(normalized);
            }
        }

        return result;
    }

    private sealed class MaintenanceState
    {
        public int WritesSinceCompact { get; set; }
        public DateTimeOffset? LastCompactedAt { get; set; }
        public List<int>? RecentRecallHits { get; set; }
        public int CompactionRuns { get; set; }
        public int LastCompactionEntriesBefore { get; set; }
        public int LastCompactionEntriesAfter { get; set; }
        public int TotalCompactionSavedEntries { get; set; }
        public int ModelSummaryCalls { get; set; }
        public int ModelSummaryTokensUsedToday { get; set; }
        public string? ModelSummaryBudgetDate { get; set; }
    }

    private readonly record struct CompactionResult(bool Applied, int EntriesBefore, int EntriesAfter)
    {
        public static readonly CompactionResult None = new(false, 0, 0);
    }
}

public static class DualModelResolver
{
    public static DualModelRuntimeConfig ResolveForRole(PuddingCliConfig config, string role)
    {
        var template = config.AgentTemplates
            .FirstOrDefault(t => t.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
            ?? config.AgentTemplates.FirstOrDefault()
            ?? new AgentTemplateEntry
            {
                Role = role,
                Conscious = new ModelRefConfig
                {
                    ProviderId = config.ActiveProvider ?? config.Providers.FirstOrDefault()?.Id ?? "default",
                    Model = config.Providers.FirstOrDefault()?.Model ?? "gpt-4o"
                }
            };

        var conscious = ResolveModelRef(config, template.Conscious, fallbackToActive: true);
        var subconsciousRef = template.Subconscious ?? new ModelRefConfig
        {
            ProviderId = config.GlobalSubconscious?.ProviderId ?? conscious.ProviderId,
            Model = config.GlobalSubconscious?.Model ?? "gpt-4o-mini"
        };
        var subconscious = ResolveModelRef(config, subconsciousRef, fallbackToActive: false);

        var policy = template.SubconsciousPolicy;
        if (policy.BudgetRatio <= 0)
            policy.BudgetRatio = config.GlobalSubconscious?.BudgetRatio ?? 0.15;

        return new DualModelRuntimeConfig(conscious, subconscious, policy);
    }

    private static RuntimeModelRef ResolveModelRef(
        PuddingCliConfig config,
        ModelRefConfig modelRef,
        bool fallbackToActive)
    {
        var provider = config.Providers.FirstOrDefault(p =>
            p.Id.Equals(modelRef.ProviderId, StringComparison.OrdinalIgnoreCase));

        if (provider is null && fallbackToActive)
        {
            provider = config.Providers.FirstOrDefault(p =>
                p.Id.Equals(config.ActiveProvider, StringComparison.OrdinalIgnoreCase))
                ?? config.Providers.FirstOrDefault();
        }

        var providerId = provider?.Id ?? modelRef.ProviderId;
        var model = !string.IsNullOrWhiteSpace(modelRef.Model)
            ? modelRef.Model
            : provider?.Model ?? "gpt-4o-mini";

        return new RuntimeModelRef(providerId, model);
    }
}
