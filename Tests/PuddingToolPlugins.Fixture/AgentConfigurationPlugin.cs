using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.AgentSdk;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;

namespace Sample.PluginFixture;

public sealed class AgentConfigurationPlugin : ToolPlugin
{
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _deferredResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override IReadOnlyCollection<string> ToolIds => ["configuration"];
    public override async ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
    {
        var configuration = Context.AgentConfiguration();
        var action = call.Arguments.GetProperty("action").GetString();
        if (action == "defer")
        {
            _ = Task.Run(async () =>
            {
                await _resume.Task;
                try { await configuration.GetAsync(); _deferredResult.TrySetResult("unexpected-access"); }
                catch (ConfigurationApiException ex) { _deferredResult.TrySetResult(ex.Code); }
            });
            return PluginResult.Ok(new { scheduled = true });
        }
        if (action == "resume")
        {
            _resume.TrySetResult();
            return PluginResult.Ok(new { code = await _deferredResult.Task.WaitAsync(ct) });
        }
        if (action == "forge")
        {
            var transport = (IPluginAgentApiTransport)Context.Core;
            return PluginResult.Ok(await transport.InvokeAgentApiAsync("agent.configuration.read", PluginJson.Value(new { agentId = "another" }), ct));
        }
        if (action == "read") return PluginResult.Ok(await configuration.GetAsync(ct));
        if (action == "changes") return PluginResult.Ok(await configuration.ReadChangesAsync(ct: ct));
        var options = new ConfigurationUpdateOptions(call.Arguments.GetProperty("revision").GetString()!, call.Arguments.GetProperty("operationId").GetString()!);
        var receipt = await configuration.UpdateAsync(new() { DisplayName = call.Arguments.GetProperty("name").GetString() }, options, ct);
        return PluginResult.Ok(receipt);
    }
}
