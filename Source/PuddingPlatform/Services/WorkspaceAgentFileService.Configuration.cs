using System.Collections.Immutable;
using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingAgentConfiguration;
using PuddingAgentConfiguration.Contracts;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>Configuration adapter shares the existing manifest writer lock; there is no second configuration store.</summary>
public sealed partial class WorkspaceAgentFileService : IAgentConfigurationStore
{
    async Task<AgentConfigurationRecord> IAgentConfigurationStore.ReadAsync(AgentRef agent, CancellationToken ct)
    {
        AgentConfigurationService.ValidateReference(agent);
        await _writeLock.WaitAsync(ct);
        try { return ReadConfigurationRecord(await LoadConfigurationManifestAsync(agent, ct), agent); }
        finally { _writeLock.Release(); }
    }

    async Task<T> IAgentConfigurationStore.TransactAsync<T>(AgentRef agent,
        Func<AgentConfigurationRecord, ConfigurationTransaction<T>> transaction, CancellationToken ct)
    {
        AgentConfigurationService.ValidateReference(agent);
        await _writeLock.WaitAsync(ct);
        try
        {
            var manifest = await LoadConfigurationManifestAsync(agent, ct);
            var before = ReadConfigurationRecord(manifest, agent);
            var result = transaction(before);
            if (!result.Write) return result.Result;
            var next = result.State;
            if (next.Snapshot.Agent != agent || next.Snapshot.TemplateId != before.Snapshot.TemplateId)
                throw new ConfigurationApiException("store.invalid", "A configuration transaction cannot change identity or template ownership.");
            var updated = manifest with { DisplayName = next.Snapshot.DisplayName, Description = next.Snapshot.Description };
            var journal = new ConfigurationJournal(1, ConfigurationFingerprint(updated), next.Snapshot.Revision, next.Operations, next.Changes);
            updated = updated with { ConfigurationControl = JsonSerializer.SerializeToElement(journal, JsonOptions) };
            ct.ThrowIfCancellationRequested();
            try
            {
                // Snapshot fields, receipt and change event are replaced in the SAME manifest.
                await AtomicFileWriter.WriteJsonAsync(Path.Combine(ResolveAgentInstanceRoot(agent.AgentId), "manifest.json"), updated, JsonOptions, ct);
            }
            catch (IOException)
            {
                throw new ConfigurationApiException("operation.unknown", "The configuration write could not be confirmed; retry the exact request with the same OperationId.", "unknown");
            }
            return result.Result;
        }
        finally { _writeLock.Release(); }
    }

    private async Task<AgentInstanceManifest> LoadConfigurationManifestAsync(AgentRef agent, CancellationToken ct)
    {
        var reference = await LoadWorkspaceRefAsync(agent.WorkspaceId, agent.AgentId, ct);
        var manifest = await LoadInstanceManifestAsync(agent.AgentId, ct);
        // Explicit scope check, independent of file paths and DTO projection.
        if (reference is null || manifest is null || reference.AgentInstanceId != agent.AgentId
            || reference.WorkspaceId != agent.WorkspaceId || manifest.AgentInstanceId != agent.AgentId
            || manifest.WorkspaceId != agent.WorkspaceId)
            throw new ConfigurationApiException("resource.not_found", "Agent was not found in the requested workspace.");
        return manifest;
    }

    private static AgentConfigurationRecord ReadConfigurationRecord(AgentInstanceManifest manifest, AgentRef agent)
    {
        ConfigurationJournal? journal = null;
        if (manifest.ConfigurationControl is { } json && json.ValueKind != JsonValueKind.Null)
        {
            try { journal = json.Deserialize<ConfigurationJournal>(JsonOptions); }
            catch (JsonException) { throw new ConfigurationApiException("store.invalid", "The configuration journal is invalid; no data was changed."); }
            if (journal is null || journal.Version != 1 || journal.Operations.IsDefault || journal.Changes.IsDefault
                || string.IsNullOrEmpty(journal.Revision) || string.IsNullOrEmpty(journal.ContentHash))
                throw new ConfigurationApiException("store.invalid", "The configuration journal is unsupported or incomplete.");
        }
        var hash = ConfigurationFingerprint(manifest);
        // Legacy writers preserve the journal but may change any manifest field. Detect that instead of trusting a stale revision.
        var revision = journal?.ContentHash == hash ? journal.Revision : "content:" + hash;
        return new(new(agent, revision, manifest.DisplayName, manifest.Description, manifest.TemplateId, DateTimeOffset.UtcNow),
            journal?.Operations ?? [], journal?.Changes ?? []);
    }

    private static string ConfigurationFingerprint(AgentInstanceManifest manifest) =>
        ComputeSha256(JsonSerializer.Serialize(manifest with { ConfigurationControl = null }, JsonOptions));

    private sealed record ConfigurationJournal(int Version, string ContentHash, string Revision,
        ImmutableArray<RecordedConfigurationOperation> Operations, ImmutableArray<AgentConfigurationChanged> Changes);
}
