using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;

namespace Sample.TextTools;

public sealed class TextToolsPlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => ["text_stats", "read_text"];
    public override IReadOnlyCollection<string> HookIds => ["failure_advice"];
    public override async ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
    {
        if (call.ToolId == "read_text")
            return PluginResult.Ok(await Context.Core.ReadWorkspaceTextAsync(call.Arguments.GetProperty("path").GetString()!, ct));
        var text = call.Arguments.GetProperty("text").GetString()!;
        return PluginResult.Ok(new { characters = text.EnumerateRunes().Count(), lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1 });
    }
    public override ValueTask<PluginHookResult> OnToolResultAsync(PluginHookCall call, CancellationToken ct)
        => ValueTask.FromResult(new PluginHookResult(call.Success ? null : "先核对本次工具参数和原始错误，再决定是否重试。"));
}
