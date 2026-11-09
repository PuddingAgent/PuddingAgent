using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pudding.Api.Contracts;
using PuddingAgentConfiguration.Contracts;
using PuddingAgentDelegation;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatform.Services.Plugins;

/// <summary>Composition adapter to the same configuration service/store as management HTTP. Never impersonates a user.</summary>
public sealed class PluginAgentApiLeaseFactory(IAgentConfigurationStore store) : IPluginAgentApiLeaseFactory
{
    public IPluginAgentApiLease Open(PluginAgentApiGrant grant)
    {
        if (new[] { grant.PluginId, grant.ActivationId, grant.CallId, grant.Scope.SessionId }.Any(string.IsNullOrWhiteSpace))
            throw new PluginException("plugin.grant_invalid", "authorize", "Host invocation identity is required.");
        // Stable across reloads so an uncertain operation can be replayed after reauthorization.
        // The activation ID grants permission for this invocation; it is not a new dedup namespace.
        var actor = "plugin:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
            { grant.PluginId, grant.Scope.WorkspaceId, grant.Scope.AgentId, grant.Scope.SessionId })));
        try
        {
            var agent = new AgentRef(grant.Scope.WorkspaceId, grant.Scope.AgentId);
            return new Lease(new(store, new(actor, agent, grant.Deadline, grant.Read, grant.Update, grant.ReadChanges)), agent);
        }
        catch (ArgumentException) { throw new PluginException("plugin.grant_invalid", "authorize", "A valid, bounded invocation grant is required."); }
        catch (ConfigurationApiException ex) { throw new PluginException(ex.Code, "authorize", ex.Message, ex.EffectOutcome); }
    }
    private sealed class Lease(DelegatedAgentConfiguration api, AgentRef agent) : IPluginAgentApiLease
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
        public async Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken ct)
        {
            try
            {
                switch (method)
                {
                    case "agent.configuration.read":
                        Parse<Empty>(arguments);
                        return PluginJson.Value(await api.GetAsync(agent, ct));
                    case "agent.configuration.update":
                        var update = Parse<ConfigurationUpdateRequest>(arguments);
                        return PluginJson.Value(await api.UpdateAsync(agent, update.Patch, update.Options, ct));
                    case "agent.configuration.changes":
                        var page = Parse<Changes>(arguments);
                        return PluginJson.Value(await api.ReadChangesAsync(agent, page.AfterSequence, page.Limit, ct));
                    default: throw new PluginException("capability.unsupported", "core_callback", "This Agent API method is not supported.");
                }
            }
            catch (ConfigurationApiException ex) { throw new PluginException(ex.Code, "core_callback", ex.Message, ex.EffectOutcome); }
            catch (JsonException) { throw new PluginException("request.invalid", "core_callback", "Invalid or unknown Agent API fields."); }
        }
        private static T Parse<T>(JsonElement args) => args.ValueKind == JsonValueKind.Object
            ? args.Deserialize<T>(Json) ?? throw new JsonException() : throw new JsonException();
        private sealed record Empty;
        private sealed record Changes(long AfterSequence, int Limit);
        public ValueTask DisposeAsync() => api.DisposeAsync();
    }
}
