using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingHost.Hosting;
using PuddingRuntime.Services.Tools;

namespace PuddingHost.Tests.Tools;

/// <summary>
/// DeepSeek Search / Fetch 接入产品组合根（S5）的证据：
/// DesktopChild 组合通过 assembly scan 自动登记两个工具、注册表可见、权限分类为 Low / AutoAllowed / 免运行时授权。
/// </summary>
[Collection("Pudding application host composition")]
public sealed class DeepSeekWebToolsCompositionTests
{
    [Fact]
    public async Task DesktopChild_CompositionRoot_RegistersDeepSeekWebTools_AsAutoAllowedTools()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"deepseek-web-composition-{Guid.NewGuid():N}");

        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18081",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            // 1) 两个工具在产品组合根下经 contextual host package 暴露（非 Kernel 工具不以 IPuddingTool
            //    身份登记，见 HostToolContributions.RegisterBinding），所以断言从 registry 取且带 capability policy。
            var capability = new CapabilityPolicy { AllowedToolNames = ["deepseek_search", "deepseek_fetch"] };
            var context = new ToolExecutionContext
            {
                WorkspaceId = "test", SessionId = "test", AgentInstanceId = "test", CapabilityPolicy = capability,
            };
            var registry = app.Services.GetRequiredService<IPuddingToolRegistry>();
            var available = registry.ListAvailable(capability, context)
                .Select(descriptor => descriptor.ToolId)
                .ToArray();
            Assert.Contains("deepseek_search", available);
            Assert.Contains("deepseek_fetch", available);

            // registry 的 contextual 路径返回 MountedTool 包装，只用于断言可见性；实例与描述符
            // 从 DI 的具体类型登记取（binding 会补 type→type 单例）。
            Assert.NotNull(registry.GetTool("deepseek_search", context));
            Assert.NotNull(registry.GetTool("deepseek_fetch", context));
            var searchTool = app.Services.GetRequiredService<DeepSeekSearchTool>();
            var fetchTool = app.Services.GetRequiredService<DeepSeekFetchTool>();
            Assert.Same(searchTool, app.Services.GetRequiredService<DeepSeekSearchTool>());
            Assert.Same(fetchTool, app.Services.GetRequiredService<DeepSeekFetchTool>());

            // 3) 用户裁定：工具风险权限 = 低风险、无需授权（AutoAllowed）。
            var policy = new ToolPermissionPolicyService();
            foreach (var (toolId, descriptor) in new[]
                     {
                         ("deepseek_search", searchTool.Descriptor),
                         ("deepseek_fetch", fetchTool.Descriptor),
                     })
            {
                Assert.Equal(toolId, descriptor.ToolId);
                Assert.Equal(ToolCategory.Network, descriptor.Category);
                Assert.Equal(ToolPermissionLevel.Low, descriptor.PermissionLevel);
                Assert.True(descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly), toolId);
                Assert.True(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe), toolId);
                Assert.True(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork), toolId);
                Assert.True(descriptor.IsEnabledByDefault, toolId);

                var decision = policy.Classify(descriptor);
                Assert.Equal(ToolPermissionTier.AutoAllowed, decision.Tier);
                Assert.False(decision.RequiresRuntimeAuthorization, toolId);
                Assert.True(decision.RequiresNetworkAccess, toolId);
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
