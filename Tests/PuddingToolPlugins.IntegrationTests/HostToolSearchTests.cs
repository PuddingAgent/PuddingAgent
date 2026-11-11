using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Tools;

namespace PuddingToolPlugins.IntegrationTests;

/// <summary>
/// plugin_search must cover both inventories. A caller searching "shell" has to find pudding.shell,
/// which lives in the declarative host-package directory and never passes through plugin_import.
/// </summary>
[TestClass]
public sealed class HostToolSearchTests
{
    private const string ExportId = "auto.mount.probe";
    private const string ContentHash = "SEARCH-PROBE-HASH";

    [TestMethod]
    public async Task HostPackagesAreSearchable_AndActiveReflectsAutoMount()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-search-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var provider = Build();
            var host = new HostToolContributions(provider, [Package()]);
            using var service = new ExecutablePluginService(PuddingDataPaths.FromRoot(root));
            var tool = new PluginSearchTool(service, host);

            var before = await Search(tool, "probe");
            Assert.AreEqual(1, before.GetProperty("matched").GetInt32());
            var item = before.GetProperty("items")[0];
            Assert.AreEqual("host-service", item.GetProperty("kind").GetString());
            Assert.AreEqual("pudding.probe", item.GetProperty("id").GetString());
            Assert.IsFalse(item.GetProperty("active").GetBoolean(), "not mounted yet");

            // 首次枚举即自动挂载（732b2ea 的行为），随后 active 必须变为 true
            Assert.HasCount(1, host.ListTools(Context()));
            var after = await Search(tool, "probe");
            Assert.IsTrue(after.GetProperty("items")[0].GetProperty("active").GetBoolean(), "auto-mount must be visible");

            // 导出工具名也参与匹配
            var byTool = await Search(tool, ExportId);
            Assert.AreEqual(1, byTool.GetProperty("matched").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<JsonElement> Search(PluginSearchTool tool, string query)
    {
        var result = await tool.ExecuteAsync(new()
        {
            Context = Context(),
            ToolCallId = Guid.NewGuid().ToString("N"),
            ArgumentsJson = JsonSerializer.Serialize(new { query }),
        });
        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(result.Output!);
        return document.RootElement.Clone();
    }

    private static HostToolPackage Package() => new(
        "pudding.probe", "1.0.0",
        new Dictionary<string, string>(StringComparer.Ordinal) { [ExportId] = typeof(ProbeTool).FullName! },
        ContentHash);

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = "workspace",
        SessionId = "session",
        AgentInstanceId = "agent",
        CapabilityPolicy = new CapabilityPolicy { AllowedToolNames = [ExportId] },
    };

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolPermissionPolicyService>(new AllowAllPolicy());
        HostToolContributions.Register(services);
        services.AddPuddingTool<ProbeTool>();
        return services.BuildServiceProvider();
    }

    public sealed class ProbeTool : IPuddingTool
    {
        public ToolDescriptor Descriptor => new() { ToolId = ExportId, Name = "Probe", Description = "Search host fixture" };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ToolExecutionResult { Success = true });
    }

    private sealed class AllowAllPolicy : IToolPermissionPolicyService
    {
        public ToolPermissionDecision Classify(ToolDescriptor descriptor) => throw new NotSupportedException();

        public bool RequiresRuntimeAuthorization(ToolDescriptor descriptor) => false;

        public bool CanExposeToAgent(ToolDescriptor descriptor, CapabilityPolicy? policy) => true;

        public CapabilityPolicy BuildCapabilityPolicy(
            IEnumerable<ToolDescriptor> descriptors,
            IEnumerable<string> selectedToolNames,
            bool isTaskRole) => throw new NotSupportedException();
    }
}
