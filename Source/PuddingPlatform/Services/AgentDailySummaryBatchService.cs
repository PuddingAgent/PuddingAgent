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
    ILLMConfigResolver llmConfigResolver)
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
                var memoryConfig = await ResolveMemoryConfigAsync(manifest, day, ct);
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
        string day,
        CancellationToken ct)
    {
        var route = await llmConfigResolver.ResolveRoleAsync(
            manifest.WorkspaceId ?? "default",
            manifest.AgentInstanceId,
            AgentLlmRoleIds.Subconscious,
            ct);
        return new MemoryLlmConfig(
            route.Config.Endpoint,
#pragma warning disable CS0618
            route.Config.ApiKey,
#pragma warning restore CS0618
            route.ModelId)
        {
            ProviderId = route.ProviderId,
            ProfileId = route.ProfileId,
            WorkspaceId = manifest.WorkspaceId ?? "default",
            SessionId = $"daily-summary:{day}",
            AgentInstanceId = manifest.AgentInstanceId,
            Stage = "daily-summary",
        };
    }

    private sealed record AgentDailySummaryManifest(
        string AgentInstanceId,
        string? WorkspaceId,
        string? TemplateId);
}
