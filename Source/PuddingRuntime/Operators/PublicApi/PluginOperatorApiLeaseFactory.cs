using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.Contracts;
using PuddingOperatorAccess;
using PuddingOperatorAccess.Contracts;

namespace PuddingRuntime.Operators.PublicApi;

public sealed class PluginOperatorApiLeaseFactory(OperatorAccessService service) : IPluginOperatorApiLeaseFactory
{
    public IPluginOperatorApiLease Open(PluginOperatorGrant grant)
    {
        if (new[] { grant.PluginId, grant.ActivationId, grant.CallId }.Any(string.IsNullOrWhiteSpace))
            throw new PluginException("plugin.grant_invalid", "authorize", "A host invocation identity is required.");
        var caller = "plugin:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { grant.PluginId, grant.ActivationId, grant.CallId })));
        try
        {
            return new Lease(service.Open(new(caller, new(grant.Scope.WorkspaceId, grant.Scope.AgentId, grant.Scope.SessionId),
                grant.Deadline, grant.CanList, grant.CanScoreSkillValue ? [SkillValueApi.SceneKey] : [])));
        }
        catch (ArgumentException) { throw new PluginException("plugin.grant_invalid", "authorize", "A bounded host invocation is required."); }
    }
    private sealed class Lease(IOperatorLease api) : IPluginOperatorApiLease
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
        public async Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken ct)
        {
            try
            {
                if (arguments.ValueKind != JsonValueKind.Object) throw new JsonException();
                switch (method)
                {
                    case "operators.list":
                        _ = arguments.Deserialize<Empty>(Json) ?? throw new JsonException();
                        return PluginJson.Value(await api.ListAsync(ct));
                    case "operators.skill-value.score":
                        return PluginJson.Value(await api.ScoreAsync(arguments.Deserialize<OperatorScoreRequest>(Json) ?? throw new JsonException(), ct));
                    default: throw new PluginException("capability.unsupported", "core_callback", "This operator API is not supported.");
                }
            }
            catch (OperatorApiException ex) { throw new PluginException(ex.Code, "core_callback", ex.Message); }
            catch (JsonException) { throw new PluginException("request.invalid", "core_callback", "Invalid or unknown operator request fields."); }
        }
        private sealed record Empty;
        public ValueTask DisposeAsync() => api.DisposeAsync();
    }
}
