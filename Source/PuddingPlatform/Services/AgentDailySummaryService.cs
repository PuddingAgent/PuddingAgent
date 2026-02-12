using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

public sealed class AgentDailySummaryService(
    PuddingDataPaths paths,
    ISubconsciousTextProcessingService textProcessing)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<AgentDailySummaryResult> GenerateAsync(
        AgentDailySummaryGenerateRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Day);

        var summaryRoot = paths.AgentInstanceDailySummaryRoot(request.AgentInstanceId);
        var memoryRoot = paths.AgentInstanceMemoryRoot(request.AgentInstanceId);
        Directory.CreateDirectory(summaryRoot);
        Directory.CreateDirectory(memoryRoot);

        var summaryPath = Path.Combine(summaryRoot, $"{request.Day}.md");
        var indexPath = paths.AgentInstanceMemoryIndexFile(request.AgentInstanceId);
        var source = await ReadOrdinaryLogSourceAsync(request.AgentInstanceId, request.Day, ct);

        if (source.SessionIds.Count == 0 || string.IsNullOrWhiteSpace(source.Markdown))
        {
            return new AgentDailySummaryResult(
                request.AgentInstanceId,
                request.Day,
                summaryPath,
                indexPath,
                string.Empty,
                Skipped: true,
                source.SessionIds);
        }

        var sourceHash = ComputeSha256(source.Markdown);
        var index = await ReadIndexAsync(indexPath, request.AgentInstanceId, ct);
        var existing = index.DailySummaries.FirstOrDefault(entry => entry.Day == request.Day);
        if (existing is not null
            && existing.SourceHash == sourceHash
            && File.Exists(summaryPath))
        {
            return new AgentDailySummaryResult(
                request.AgentInstanceId,
                request.Day,
                summaryPath,
                indexPath,
                sourceHash,
                Skipped: true,
                source.SessionIds);
        }

        var summary = await textProcessing.SummarizeDailyLogAsync(
            new DailyLogSummaryRequest(
                request.WorkspaceId,
                request.AgentInstanceId,
                request.AgentTemplateId,
                request.Day,
                source.Markdown,
                request.MemoryLlmConfig),
            ct);

        await File.WriteAllTextAsync(summaryPath, summary.Trim(), Encoding.UTF8, ct);

        var updatedEntry = new AgentDailySummaryIndexEntry(
            request.Day,
            summaryPath,
            sourceHash,
            source.SessionIds,
            DateTimeOffset.UtcNow);

        var dailySummaries = index.DailySummaries
            .Where(entry => entry.Day != request.Day)
            .Append(updatedEntry)
            .OrderByDescending(entry => entry.Day, StringComparer.Ordinal)
            .ToArray();

        var updatedIndex = new AgentMemoryIndex(
            request.AgentInstanceId,
            DateTimeOffset.UtcNow,
            dailySummaries);

        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize(updatedIndex, JsonOptions),
            Encoding.UTF8,
            ct);

        return new AgentDailySummaryResult(
            request.AgentInstanceId,
            request.Day,
            summaryPath,
            indexPath,
            sourceHash,
            Skipped: false,
            source.SessionIds);
    }

    private async Task<OrdinaryLogSource> ReadOrdinaryLogSourceAsync(
        string agentInstanceId,
        string day,
        CancellationToken ct)
    {
        var dayRoot = paths.AgentInstanceMessageLogDayRoot(agentInstanceId, day);
        if (!Directory.Exists(dayRoot))
            return new OrdinaryLogSource(string.Empty, Array.Empty<string>());

        var files = Directory
            .EnumerateFiles(dayRoot, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        var sessionIds = new List<string>(files.Length);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var sessionId = Path.GetFileNameWithoutExtension(file);
            sessionIds.Add(sessionId);
            builder
                .AppendLine($"# Session: {sessionId}")
                .AppendLine()
                .AppendLine(content.Trim())
                .AppendLine();
        }

        return new OrdinaryLogSource(builder.ToString().Trim(), sessionIds);
    }

    private static async Task<AgentMemoryIndex> ReadIndexAsync(
        string indexPath,
        string agentInstanceId,
        CancellationToken ct)
    {
        if (!File.Exists(indexPath))
            return AgentMemoryIndex.Empty(agentInstanceId);

        try
        {
            await using var stream = File.OpenRead(indexPath);
            var index = await JsonSerializer.DeserializeAsync<AgentMemoryIndex>(stream, JsonOptions, ct);
            return index is null || string.IsNullOrWhiteSpace(index.AgentInstanceId)
                ? AgentMemoryIndex.Empty(agentInstanceId)
                : index;
        }
        catch (JsonException)
        {
            return AgentMemoryIndex.Empty(agentInstanceId);
        }
    }

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record OrdinaryLogSource(string Markdown, IReadOnlyList<string> SessionIds);
}

public sealed record AgentDailySummaryGenerateRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string Day,
    MemoryLlmConfig? MemoryLlmConfig);

public sealed record AgentDailySummaryResult(
    string AgentInstanceId,
    string Day,
    string SummaryPath,
    string IndexPath,
    string SourceHash,
    bool Skipped,
    IReadOnlyList<string> SourceSessionIds);

public sealed record AgentMemoryIndex(
    string AgentInstanceId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AgentDailySummaryIndexEntry> DailySummaries)
{
    public static AgentMemoryIndex Empty(string agentInstanceId) =>
        new(agentInstanceId, DateTimeOffset.UtcNow, Array.Empty<AgentDailySummaryIndexEntry>());
}

public sealed record AgentDailySummaryIndexEntry(
    string Day,
    string SummaryPath,
    string SourceHash,
    IReadOnlyList<string> SourceSessionIds,
    DateTimeOffset UpdatedAt);
