using PuddingCode.Tools;

namespace PuddingRuntime.Services.Plugins;

public sealed class ExecutablePluginToolSource(ExecutablePluginService plugins) : IContextualPuddingToolSource
{
    public long Revision => plugins.Revision;
    public IReadOnlyList<IPuddingTool> ListTools(ToolExecutionContext context) => plugins.ListTools(context)
        .Where(tool => (!tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell) || context.CapabilityPolicy?.AllowShellExecution == true)
            && (!tool.Descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite) || context.CapabilityPolicy?.AllowFileWrite == true))
        .ToArray();
}
