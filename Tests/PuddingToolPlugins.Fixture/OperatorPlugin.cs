using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.AgentSdk;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;

namespace Sample.PluginFixture;

public sealed class OperatorPlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => ["score"];
    public override async ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
    {
        var action = call.Arguments.GetProperty("action").GetString();
        if (action == "list") return PluginResult.Ok(await Context.Operators().ListAsync(ct));
        if (action == "read-state") return PluginResult.Ok(new { value = await Context.Core.ReadStateAsync("marker", ct) });
        if (action == "write-then-score") await Context.Core.WriteStateAsync("marker", "written", ct);
        if (action == "forge")
            return PluginResult.Ok(await ((IPluginAgentApiTransport)Context.Core).InvokeAgentApiAsync("operators.list", PluginJson.Value(new { scope = new { agentId = "other" } }), ct));
        var input = call.Arguments.GetProperty("input").Deserialize<SkillValueInput>(PluginJson.Options)!;
        return PluginResult.Ok(await Context.Operators().SkillValue.ScoreAsync(input, new("score-call"), ct));
    }
}
