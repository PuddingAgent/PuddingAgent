using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Samples.AgentConfigurationClient;

/// <summary>
/// Read-only sample entry point: resolves one Agent through the public SDK facade and prints
/// exactly one JSON line describing its configuration projection.
/// It never calls UpdateAsync or ReadChangesAsync, never reads a manifest/JSON/database file,
/// never creates an Agent, and does not own or dispose the injected API or writers.
/// </summary>
public static class ConfigurationInspector
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(IAgentConfigurationApi api, string workspaceId, string agentId,
        TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            // Agents.GetAsync verifies existence/authorization; Configuration.GetAsync reads the projection.
            var agent = await new PuddingClient(api).Workspace(workspaceId).Agents.GetAsync(agentId, ct).ConfigureAwait(false);
            var snapshot = await agent.Configuration.GetAsync(ct).ConfigureAwait(false);
            var line = JsonSerializer.Serialize(new ConfigurationLine(workspaceId, agentId, snapshot.Revision,
                snapshot.DisplayName, snapshot.Description, snapshot.TemplateId), OutputJson);
            await output.WriteLineAsync(line).ConfigureAwait(false);
            return 0;
        }
        catch (ConfigurationApiException exception)
        {
            // Only the stable code belongs on stderr; no configuration values, revision or message body.
            await error.WriteLineAsync(exception.Code).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
    }

    private sealed record ConfigurationLine(string WorkspaceId, string AgentId, string Revision,
        string? DisplayName, string? Description, string TemplateId);
}
