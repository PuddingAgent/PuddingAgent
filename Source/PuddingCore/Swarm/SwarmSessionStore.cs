using System.Text.Json;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

internal sealed class SwarmSessionStore
{
    private readonly string _runtimeDir;
    private readonly string _latestPath;

    public SwarmSessionStore(string swarmRoot)
    {
        _runtimeDir = Path.Combine(swarmRoot, "runtime");
        _latestPath = Path.Combine(_runtimeDir, "latest-session.json");
    }

    public async Task SaveAsync(SwarmSessionState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_runtimeDir);
        state.UpdatedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_latestPath, json, ct);
    }

    public async Task<SwarmSessionState?> LoadLatestAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_latestPath))
            return null;
        try
        {
            var json = await File.ReadAllTextAsync(_latestPath, ct);
            return JsonSerializer.Deserialize<SwarmSessionState>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> HasPendingTasksAsync(CancellationToken ct = default)
    {
        var state = await LoadLatestAsync(ct);
        if (state is null) return false;
        return state.Tasks.Any(t => t.Status is SwarmTaskStatus.Created
            or SwarmTaskStatus.Assigned
            or SwarmTaskStatus.InProgress
            or SwarmTaskStatus.PendingReview
            or SwarmTaskStatus.Testing
            or SwarmTaskStatus.Blocked);
    }
}

