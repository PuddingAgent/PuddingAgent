using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

public sealed class AgentDailySummaryBatchService(
    PuddingDataPaths paths,
    AgentDailySummaryService summaryService,
    ILogger<AgentDailySummaryBatchService> logger,
    ILLMConfigResolver? llmConfigResolver = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<IReadOnlyList<AgentDailySummaryResult>> GeneratePreviousDayAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var day = now.AddDays(-1).ToString("yyyy-MM-dd");
        return GenerateForDayAsync(day, ct);
    }

    public async Task<IReadOnlyList<AgentDailySummaryResult>> GenerateForDayAsync(
        string day,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(day);

        var agents = DiscoverAgentsWithMessageLogs(day);
        if (agents.Count == 0)
        {
            logger.LogDebug("[AgentDailySummaryBatch] No agent message logs found for day={Day}", day);
            return Array.Empty<AgentDailySummaryResult>();
        }

        var results = new List<AgentDailySummaryResult>(agents.Count);
        foreach (var agentInstanceId in agents)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var manifest = await ReadManifestAsync(agentInstanceId, ct);
                var memoryConfig = await ResolveMemoryConfigAsync(manifest, ct);
                var result = await summaryService.GenerateAsync(
                    new AgentDailySummaryGenerateRequest(
                        WorkspaceId: manifest.WorkspaceId ?? "default",
                        AgentInstanceId: agentInstanceId,
                        AgentTemplateId: manifest.TemplateId,
                        Day: day,
                        MemoryLlmConfig: memoryConfig),
                    ct);

                results.Add(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[AgentDailySummaryBatch] Generate failed agent={AgentInstanceId} day={Day}",
                    agentInstanceId,
                    day);
            }
        }

        logger.LogInformation(
            "[AgentDailySummaryBatch] Completed day={Day} discovered={Discovered} generated={Generated}",
            day,
            agents.Count,
            results.Count);

        return results;
    }

    private IReadOnlyList<string> DiscoverAgentsWithMessageLogs(string day)
    {
        if (!Directory.Exists(paths.AgentInstancesRoot))
            return Array.Empty<string>();

        return Directory
            .EnumerateDirectories(paths.AgentInstancesRoot)
            .Select(Path.GetFileName)
            .Where(agentId => !string.IsNullOrWhiteSpace(agentId))
            .Cast<string>()
            .Where(agentId =>
            {
                var dayRoot = paths.AgentInstanceMessageLogDayRoot(agentId, day);
                return Directory.Exists(dayRoot)
                    && Directory.EnumerateFiles(dayRoot, "*.md", SearchOption.TopDirectoryOnly).Any();
            })
            .OrderBy(agentId => agentId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<AgentDailySummaryManifest> ReadManifestAsync(
        string agentInstanceId,
        CancellationToken ct)
    {
        var manifestPath = Path.Combine(paths.AgentInstanceRoot(agentInstanceId), "manifest.json");
        if (!File.Exists(manifestPath))
            return new AgentDailySummaryManifest(agentInstanceId, "default", null);

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<AgentInstanceManifest>(stream, JsonOptions, ct);
            return new AgentDailySummaryManifest(
                string.IsNullOrWhiteSpace(manifest?.AgentInstanceId) ? agentInstanceId : manifest.AgentInstanceId,
                string.IsNullOrWhiteSpace(manifest?.WorkspaceId) ? "default" : manifest.WorkspaceId,
                string.IsNullOrWhiteSpace(manifest?.TemplateId) ? null : manifest.TemplateId);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "[AgentDailySummaryBatch] Invalid manifest agent={AgentInstanceId}; fallback to defaults",
                agentInstanceId);
            return new AgentDailySummaryManifest(agentInstanceId, "default", null);
        }
    }

    private async Task<MemoryLlmConfig?> ResolveMemoryConfigAsync(
        AgentDailySummaryManifest manifest,
        CancellationToken ct)
    {
        if (llmConfigResolver is null || string.IsNullOrWhiteSpace(manifest.TemplateId))
            return null;

        try
        {
            var cfg = await llmConfigResolver.ResolveMemoryAsync(manifest.TemplateId, manifest.WorkspaceId, ct);
            if (cfg is null
                || string.IsNullOrWhiteSpace(cfg.Endpoint)
                || string.IsNullOrWhiteSpace(cfg.ModelId))
            {
                return null;
            }

            return new MemoryLlmConfig(cfg.Endpoint, cfg.ApiKey ?? string.Empty, cfg.ModelId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[AgentDailySummaryBatch] Resolve memory LLM config failed agent={AgentInstanceId} template={TemplateId}",
                manifest.AgentInstanceId,
                manifest.TemplateId);
            return null;
        }
    }

    private sealed record AgentDailySummaryManifest(
        string AgentInstanceId,
        string? WorkspaceId,
        string? TemplateId);
}
