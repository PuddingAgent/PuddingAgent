using System.Text.Json;

namespace Pudding.ToolPlugins.Contracts;

/// <summary>Optional Core API 1.1 transport. The host still validates every method and argument.</summary>
public interface IPluginAgentApiTransport
{
    ValueTask<JsonElement> InvokeAgentApiAsync(string method, JsonElement arguments, CancellationToken ct = default);
}

/// <summary>Host-authored invocation identity; never deserialized from a plugin request.</summary>
public sealed record PluginAgentApiGrant(PluginScope Scope, string PluginId, string ActivationId,
    string CallId, DateTimeOffset Deadline, bool Read, bool Update, bool ReadChanges);

/// <summary>Host adapter port. No IServiceProvider, token, configuration file or database crosses the worker boundary.</summary>
public interface IPluginAgentApiLeaseFactory
{
    IPluginAgentApiLease Open(PluginAgentApiGrant grant);
}
public interface IPluginAgentApiLease : IAsyncDisposable
{
    Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken ct);
}
