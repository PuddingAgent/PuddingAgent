using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingHost.Hosting;
using PuddingRuntime.Services.Tools;

namespace PuddingHost.Tests.Tools;

[Collection("Pudding application host composition")]
public sealed class BiyingFinanceToolsCompositionTests
{
    [Fact]
    public async Task DesktopChild_ResolvesFinanceToolsWithSharedRuntime_WithoutProviderConfiguration()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", $"biying-composition-{Guid.NewGuid():N}");
        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18083",
            ]);
            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            var runtime = app.Services.GetRequiredService<BiyingFinanceRuntime>();
            using var scope = app.Services.CreateScope();
            Assert.Same(runtime, scope.ServiceProvider.GetRequiredService<BiyingFinanceRuntime>());
            Assert.Equal(PuddingDataPaths.FromRoot(dataRoot).SystemConfigFile("biying.api.json"), runtime.ConfigPath);
            Assert.False(File.Exists(runtime.ConfigPath));

            var tools = app.Services.GetServices<IPuddingTool>().ToList();
            var registry = app.Services.GetRequiredService<IPuddingToolRegistry>();
            var available = registry.ListAvailable(new CapabilityPolicy()).Select(tool => tool.ToolId).ToArray();
            foreach (var (id, type) in new (string, Type)[]
            {
                ("biying_market", typeof(BiyingMarketQueryTool)),
                ("biying_financial", typeof(BiyingFinancialQueryTool)),
                ("biying_sector", typeof(BiyingSectorQueryTool)),
                ("biying_flow", typeof(BiyingFlowQueryTool)),
                ("biying_raw", typeof(BiyingRawQueryTool)),
            })
            {
                var tool = Assert.Single(tools, tool => tool.GetType() == type);
                Assert.Contains(id, available);
                Assert.Same(tool, registry.GetTool(id));
                // Quota and cache must be shared by all four real consumers.
                var runtimeField = type.GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(runtimeField);
                Assert.Same(runtime, runtimeField.GetValue(tool));

                // Optional provider configuration must fail at invocation, never at host startup.
                var result = await tool.ExecuteAsync(new ToolExecutionRequest
                {
                    ToolCallId = $"composition-{id}",
                    ArgumentsJson = "{}",
                    Context = new ToolExecutionContext
                    {
                        WorkspaceId = "test",
                        SessionId = "test",
                        AgentInstanceId = "test",
                    },
                });
                Assert.False(result.Success);
                Assert.Contains("必盈配置缺失", result.Error);
                Assert.Contains(runtime.ConfigPath, result.Error);
            }
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
