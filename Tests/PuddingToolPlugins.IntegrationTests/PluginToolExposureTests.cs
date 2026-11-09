using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Tools;

namespace PuddingToolPlugins.IntegrationTests;

[TestClass]
public sealed class PluginToolExposureTests
{
    [TestMethod]
    public async Task PluginLoad_IsExposed_UnderTemplateGrantPolicy()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? Path.GetTempPath(),
            "exposure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var plugins = new ExecutablePluginService(PuddingDataPaths.FromRoot(root),
                new WindowsPluginWorkerFactory(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER") ?? "unused.exe"));
            var registry = new PuddingToolRegistry([new PluginLoadTool(plugins), new PluginUnloadTool(plugins)],
                contextualToolSources: [plugins]);
            var policy = new CapabilityPolicy
            {
                AllowedToolNames = ["plugin_load", "plugin_unload"],
                DefaultToolNames = ["plugin_unload"],
                RequiresGrantToolNames = ["plugin_load"],
            };
            var noAllowPolicy = new CapabilityPolicy
            {
                DefaultToolNames = ["plugin_unload"],
                RequiresGrantToolNames = ["plugin_load"],
            };
            var context = new ToolExecutionContext { WorkspaceId = "ws", SessionId = "s", AgentInstanceId = "a" };
            var schemas = new PuddingToolSchemaService(registry);
            var names = schemas.BuildLlmTools(policy, "ws", context).Select(t => t.Name).ToArray();
            var noAllowNames = schemas.BuildLlmTools(noAllowPolicy, "ws", context).Select(t => t.Name).ToArray();
            var descriptors = string.Join(";", registry.ListDescriptors()
                .Select(d => $"{d.ToolId}:{d.RuntimeStatus}:{d.PermissionLevel}:{d.PermissionOwnerToolId}"));
            CollectionAssert.Contains(names, "plugin_load",
                $"tools={string.Join(",", names)} descriptors={descriptors}");
            CollectionAssert.Contains(noAllowNames, "plugin_load");
            var restricted = policy with { AllowedToolNames = ["plugin_unload"] };
            CollectionAssert.DoesNotContain(
                schemas.BuildLlmTools(restricted, "ws", context).Select(t => t.Name).ToArray(), "plugin_load");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
