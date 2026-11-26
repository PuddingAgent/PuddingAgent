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

            // 非 Kernel 工具不再以 IPuddingTool 身份登记（HostToolContributions.RegisterBinding），
            // 只经 IContextualPuddingToolSource 暴露；而 registry 仅在**带 caller context** 时
            // 合并 contextual source（PuddingToolRegistry.BuildToolSnapshot）。断言必须走与生产
            // 调用同一个入口，否则测到的是「旧注册形态」而不是工具是否可见。
            var registry = app.Services.GetRequiredService<IPuddingToolRegistry>();
            var policy = new CapabilityPolicy
            {
                AllowedToolNames =
                [
                    "biying_market", "biying_financial", "biying_sector", "biying_flow", "biying_raw",
                ],
            };
            var context = new ToolExecutionContext
            {
                WorkspaceId = "test", SessionId = "test", AgentInstanceId = "test", CapabilityPolicy = policy,
            };
            var available = registry.ListAvailable(policy, context).Select(tool => tool.ToolId).ToArray();
            foreach (var (id, type) in new (string, Type)[]
            {
                ("biying_market", typeof(BiyingMarketQueryTool)),
                ("biying_financial", typeof(BiyingFinancialQueryTool)),
                ("biying_sector", typeof(BiyingSectorQueryTool)),
                ("biying_flow", typeof(BiyingFlowQueryTool)),
                ("biying_raw", typeof(BiyingRawQueryTool)),
            })
            {
                // registry 的 contextual 路径返回 HostToolContributions.MountedTool 包装
                // （Descriptor.SourceKind=Plugin），所以只用它断言「Agent 可见」；实例身份与
                // 共享依赖改从 DI 的具体类型登记取（RegisterBinding 会为每个 binding 补一条
                // type→type 的单例登记）。
                var mounted = registry.GetTool(id, context);
                Assert.NotNull(mounted);
                Assert.Equal(id, mounted!.Descriptor.ToolId);
                Assert.Contains(id, available);
                var tool = (IPuddingTool)app.Services.GetRequiredService(type);
                Assert.IsType(type, tool);
                Assert.Same(tool, app.Services.GetRequiredService(type));
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
