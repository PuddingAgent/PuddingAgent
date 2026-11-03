using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PuddingAgent.Tools;
using PuddingCode.Tools;
using PuddingComputer.Windows;
using PuddingHost.Hosting;
using PuddingRuntime.Services.Tools;

namespace PuddingHost.Tests.Tools;

[Collection("Pudding application host composition")]
public sealed class ComputerUseCompositionTests
{
    [Fact]
    public async Task DesktopChild_ObserveAndInputShareSession_AndAreRegisteredExactlyOnce()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", "computer-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild([
                "--desktop-child", "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot, "--urls", "http://0.0.0.0:18084"]);
            await using var app = PuddingApplicationHost.Build(PuddingApplicationHost.CreateBuilder([], options));
            var session = app.Services.GetRequiredService<ComputerDesktopSession>();
            var registry = app.Services.GetRequiredService<IPuddingToolRegistry>();
            var all = app.Services.GetServices<IPuddingTool>().ToArray();
            foreach (var (id, type) in new[] { ("computer_input", typeof(ComputerInputTool)), ("computer_observe", typeof(ComputerObserveTool)) })
            {
                var tool = Assert.Single(all, item => item.GetType() == type);
                Assert.Same(tool, registry.GetTool(id));
                var field = Assert.Single(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic), f => f.FieldType == typeof(ComputerDesktopSession));
                Assert.Same(session, field.GetValue(tool));
                if (id == "computer_input")
                {
                    var observationParameter = Assert.Single(tool.Descriptor.Parameters.Properties, p => p.Name == "observation_id");
                    Assert.Contains("Single-use", observationParameter.Description);
                    var pointParameter = Assert.Single(tool.Descriptor.Parameters.Properties, p => p.Name == "image_x");
                    Assert.Contains("mutually exclusive", pointParameter.Description);
                }
                // Invalid arguments must fail without native input or capture.
                var result = await tool.ExecuteAsync(new ToolExecutionRequest {
                    ToolCallId = id, ArgumentsJson = id == "computer_input" ? "{\"action\":\"key\",\"keys\":\"A\"}" : "{}",
                    Context = new ToolExecutionContext { WorkspaceId = "test", SessionId = "test", AgentInstanceId = "test" } });
                Assert.False(result.Success);
            }
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }
}
