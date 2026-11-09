using Pudding.ToolPlugins.Contracts;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Plugins;

public sealed class ExecutablePluginHook(ExecutablePluginService plugins) : IAgentLoopHook
{
    public async Task<SkillResult> OnToolResultTransformingAsync(AgentLoopContext context, int round,
        string toolName, string argsJson, SkillResult result, CancellationToken ct = default)
    {
        if (plugins.Revision == 0) return result;
        var advice = await plugins.Manager.AdviseAsync(new PluginHookCall("", toolName, argsJson,
            result.Success, result.Output, result.Error, result.ExitCode,
            new(context.WorkspaceId, context.AgentInstanceId, context.SessionId)), ct);
        return advice.Count == 0 ? result : result with { Output = result.Output + string.Concat(advice.Select(a =>
            $"\n\n[Plugin advice: {a.PluginId}]\n{a.Advice}")) };
    }
}
