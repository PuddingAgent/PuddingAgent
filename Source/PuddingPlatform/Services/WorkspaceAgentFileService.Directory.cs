using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// PA-02 I2 archive through the existing manifest authority: archive is a terminal flag on the same manifest (no second
/// store), fenced by the directory revision. It does not start or stop any execution.
/// </summary>
public sealed partial class WorkspaceAgentFileService
{
    public async Task<WorkspaceAgentDto> ArchiveAgentAsync(string workspaceId, string agentId, long expectedRevision,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || agentId.Length > 128 || string.IsNullOrWhiteSpace(agentId)
            || expectedRevision < 0)
            throw new ArgumentException("A bounded agent archive request is required.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInstanceManifestAsync(agentId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Agent instance '{agentId}' 不存在");
            if (!string.Equals(manifest.WorkspaceId, workspaceId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The agent belongs to another workspace.");
            var manifestPath = Path.Combine(ResolveAgentInstanceRoot(agentId), "manifest.json");
            var currentRevision = File.GetLastWriteTimeUtc(manifestPath).Ticks;
            if (expectedRevision != currentRevision)
                throw new InvalidOperationException("The agent revision changed.");
            if (manifest.IsFrozen) throw new InvalidOperationException("The agent is already archived.");
            var updated = manifest with
            {
                IsEnabled = false,
                IsFrozen = true,
            };
            ct.ThrowIfCancellationRequested();
            await AtomicFileWriter.WriteJsonAsync(
                manifestPath, updated,
                System.Text.Json.JsonSerializerOptions.Web, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
        var dto = await GetAgentAsync(workspaceId, agentId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Agent instance '{agentId}' 不存在");
        return dto;
    }
}
