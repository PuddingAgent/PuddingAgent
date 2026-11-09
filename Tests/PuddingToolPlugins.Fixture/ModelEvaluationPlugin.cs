using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.AgentSdk;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;

namespace Sample.PluginFixture;

public sealed class ModelEvaluationPlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => ["evaluate"];
    public override async ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
    {
        var action = call.Arguments.GetProperty("action").GetString();
        if (action == "list") return PluginResult.Ok(await Context.ModelEvaluations().ListAsync(ct));
        if (action == "forge") return PluginResult.Ok(await ((IPluginAgentApiTransport)Context.Core).InvokeAgentApiAsync(
            "operators.model.list", PluginJson.Value(new { parentRunId = "another-task", budget = 999999 }), ct));
        var options = new ModelEvaluationOptions(call.CallId, 64);
        if (action == "review") return PluginResult.Ok(await Context.ModelEvaluations().ReviewAsync(
            new("交付验收", [new("test", "测试通过")], [new("log", "测试通过 1/1")]), options, ct));
        return PluginResult.Ok(await Context.ModelEvaluations().ClassifyAsync(
            new("请修复这个缺陷", [new("work", "需要执行"), new("chat", "普通聊天")]), options, ct));
    }
}
