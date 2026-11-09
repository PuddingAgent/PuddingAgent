using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.AgentSdk;

public static class PluginAgentConfiguration
{
    /// <summary>Only the current execution Agent; no target identity or credentials are accepted from plugin code.</summary>
    public static AgentConfiguration AgentConfiguration(this IPluginContext context)
    {
        var agent = new AgentRef(context.Scope.WorkspaceId, context.Scope.AgentId);
        return new(new Proxy(context, agent), agent);
    }

    private sealed class Proxy(IPluginContext context, AgentRef boundAgent) : IAgentConfigurationApi
    {
        private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true };
        public async Task<AgentConfigurationSnapshot> GetAsync(AgentRef agent, CancellationToken ct = default)
        {
            EnsureScope(agent); var snapshot = await Call<AgentConfigurationSnapshot>("agent.configuration.read", new { }, false, ct);
            if (snapshot.Agent != agent || string.IsNullOrEmpty(snapshot.Revision)) throw Invalid(false);
            return snapshot;
        }
        public async Task<ConfigurationChangeReceipt> UpdateAsync(AgentRef agent, AgentConfigurationPatch patch, ConfigurationUpdateOptions options, CancellationToken ct = default)
        {
            EnsureScope(agent); var receipt = await Call<ConfigurationChangeReceipt>("agent.configuration.update", new ConfigurationUpdateRequest(patch, options), true, ct);
            if (receipt.Agent != agent || receipt.OperationId != options.OperationId || receipt.PreviousRevision != options.ExpectedRevision
                || string.IsNullOrEmpty(receipt.PersistedRevision)) throw Invalid(true);
            return receipt;
        }
        public async Task<ConfigurationChangePage> ReadChangesAsync(AgentRef agent, long afterSequence = 0, int limit = 100, CancellationToken ct = default)
        {
            EnsureScope(agent); var page = await Call<ConfigurationChangePage>("agent.configuration.changes", new { afterSequence, limit }, false, ct);
            if (page.Items.IsDefault || page.Items.Any(item => item.Agent != agent)) throw Invalid(false);
            return page;
        }
        private async Task<T> Call<T>(string method, object arguments, bool mutation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.Core is not IPluginAgentApiTransport transport)
                throw new ConfigurationApiException("capability.unsupported", "Core API 1.1 Agent configuration transport is required.");
            try
            {
                var value = await transport.InvokeAgentApiAsync(method, JsonSerializer.SerializeToElement(arguments, ResponseJson), ct);
                return value.Deserialize<T>(ResponseJson) ?? throw Invalid(mutation);
            }
            catch (PluginException ex)
            { throw new ConfigurationApiException(ex.Failure.Code, ex.Failure.Message, ex.Failure.EffectOutcome); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or OperationCanceledException)
            {
                if (!mutation && ex is OperationCanceledException) throw;
                throw Invalid(mutation);
            }
        }
        private void EnsureScope(AgentRef agent)
        {
            if (agent != boundAgent) throw new ConfigurationApiException("permission.denied", "Plugin configuration handle is bound to one Agent.");
        }
        private static ConfigurationApiException Invalid(bool mutation) => new(mutation ? "operation.unknown" : "transport.invalid_response",
            "The configuration response could not be confirmed.", mutation ? "unknown" : "none");
    }
}
