using System.Text.Json;

namespace Pudding.ToolPlugins.Contracts;

public sealed record PluginOperatorGrant(PluginScope Scope, string PluginId, string ActivationId, string CallId,
    DateTimeOffset Deadline, bool CanList, bool CanScoreSkillValue);
public interface IPluginOperatorApiLeaseFactory { IPluginOperatorApiLease Open(PluginOperatorGrant grant); }
public interface IPluginOperatorApiLease : IAsyncDisposable
{ Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken ct); }
