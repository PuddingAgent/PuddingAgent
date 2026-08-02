using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Configuration;

namespace PuddingAgent.Services;

/// <summary>
/// Composition-root-only reader for server-private Agent manifests.
/// Feishu secrets stay inside the connector factory and are never projected to API DTOs.
/// </summary>
public sealed class AgentManifestCatalog(PuddingDataPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<AgentInstanceManifest?> GetAsync(
        string agentId,
        CancellationToken ct = default)
    {
        if (!IsSafeSegment(agentId))
            return null;

        var path = Path.Combine(paths.AgentInstanceRoot(agentId), "manifest.json");
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AgentInstanceManifest>(
            stream,
            JsonOptions,
            ct);
    }

    public async Task<IReadOnlyList<AgentInstanceManifest>> ListAsync(
        CancellationToken ct = default)
    {
        if (!Directory.Exists(paths.AgentInstancesRoot))
            return [];

        var manifests = new List<AgentInstanceManifest>();
        foreach (var directory in Directory.EnumerateDirectories(paths.AgentInstancesRoot))
        {
            ct.ThrowIfCancellationRequested();
            var agentId = Path.GetFileName(directory);
            var manifest = await GetAsync(agentId, ct);
            if (manifest is not null)
                manifests.Add(manifest);
        }

        return manifests;
    }

    private static bool IsSafeSegment(string value)
        => !string.IsNullOrWhiteSpace(value)
           && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
           && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
