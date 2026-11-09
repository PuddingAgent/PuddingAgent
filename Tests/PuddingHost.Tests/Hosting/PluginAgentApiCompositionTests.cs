using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.Contracts;
using PuddingCode.Configuration;
using PuddingHost.Hosting;
using PuddingPlatform.Services.Plugins;
using PuddingRuntime.Services.Plugins;

namespace PuddingHost.Tests.Hosting;

[Collection("Pudding application host composition")]
public sealed class PluginAgentApiCompositionTests
{
    [Fact]
    public async Task DesktopChild_BindsScopedLeaseToCanonicalConfigurationStore()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", "plugin-api-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(dataRoot);
            await AtomicFileWriter.WriteJsonAsync(Path.Combine(paths.AgentInstanceRoot("agent"), "manifest.json"),
                new AgentInstanceManifest { AgentInstanceId = "agent", WorkspaceId = "workspace", TemplateId = "template", DisplayName = "Scoped Agent" }, PluginJson.Options);
            await AtomicFileWriter.WriteJsonAsync(paths.WorkspaceAgentRefFile("workspace", "agent"),
                new WorkspaceAgentRef { AgentInstanceId = "agent", WorkspaceId = "workspace" }, PluginJson.Options);
            var options = PuddingHostOptionsFactory.ForDesktopChild([
                "--desktop-child", "--desktop-parent-pid", Environment.ProcessId.ToString(), "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18080"]);
            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);
            var factory = app.Services.GetRequiredService<IPluginAgentApiLeaseFactory>();
            Assert.IsType<PluginAgentApiLeaseFactory>(factory);
            Assert.NotNull(app.Services.GetRequiredService<ExecutablePluginService>());
            await using var lease = factory.Open(new(new("workspace", "agent", "session"), "sample", "activation", "call",
                DateTimeOffset.UtcNow.AddSeconds(30), true, false, false));
            var value = await lease.CallAsync("agent.configuration.read", PluginJson.Value(new { }), default);
            Assert.Equal("Scoped Agent", value.Deserialize<AgentConfigurationSnapshot>(PluginJson.Options)!.DisplayName);
            await Assert.ThrowsAsync<PluginException>(() => lease.CallAsync("agent.configuration.read", PluginJson.Value(new { agentId = "another" }), default));
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }
}
