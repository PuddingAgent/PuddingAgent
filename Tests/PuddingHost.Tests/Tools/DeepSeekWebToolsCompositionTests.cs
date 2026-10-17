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

            // 1) 产品组合根里的 IPuddingTool 集合包含两个工具（assembly scan 自动发现，无需逐一手工登记）。
            var toolInstances = app.Services.GetServices<IPuddingTool>().ToList();
            var searchTool = Assert.Single(toolInstances.OfType<DeepSeekSearchTool>());
            var fetchTool = Assert.Single(toolInstances.OfType<DeepSeekFetchTool>());

            // 2) 注册表（Agent 可见工具清单）包含两个工具 id，且 registry 解析到同一扫描实例。
            var registry = app.Services.GetRequiredService<IPuddingToolRegistry>();
            var available = registry.ListAvailable(new CapabilityPolicy())
                .Select(descriptor => descriptor.ToolId)
                .ToArray();
            Assert.Contains("deepseek_search", available);
            Assert.Contains("deepseek_fetch", available);
            Assert.Same(searchTool, registry.GetTool("deepseek_search"));
            Assert.Same(fetchTool, registry.GetTool("deepseek_fetch"));

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
