using System.Text;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class PluginUninstallToolTests
{
    private static readonly PluginScope Scope = new("workspace", "agent", "session");

    [TestMethod]
    public async Task UninstallRemovesOnlyTheRequestedRevision_AndReportsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new ExecutablePluginService(PuddingDataPaths.FromRoot(root));
            var source = Path.Combine(root, "src");
            WritePackage(source, "sample.uninstall", "1.0.0");
            var package = await service.Packages.RegisterDirectoryAsync(source, default);
            var tool = new PluginUninstallTool(service);

            using var removed = await Run(tool, package.Manifest.Id, package.Manifest.Version, package.ContentHash);
            Assert.IsTrue(removed.RootElement.GetProperty("removed").GetBoolean());
            Assert.AreEqual("plugin.removed", removed.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(0, service.Packages.Scan().Packages.Count);

            using var again = await Run(tool, package.Manifest.Id, package.Manifest.Version, package.ContentHash);
            Assert.IsFalse(again.RootElement.GetProperty("removed").GetBoolean());
            Assert.AreEqual("plugin.not_found", again.RootElement.GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GuardBlocksEveryUnsettledState_AndAllowsOnlyUnloaded()
    {
        const string hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        foreach (var state in new[] { "Active", "Draining" })
        {
            var blocked = PluginRevisionGuard.ActiveUse([Activation(state, hash)], hash);
            Assert.IsNotNull(blocked, $"{state} must block deletion");
            StringAssert.Contains(blocked, "unload it first");
        }

        Assert.IsNull(PluginRevisionGuard.ActiveUse([Activation("Unloaded", hash)], hash), "an unloaded revision is removable");
        Assert.IsNull(PluginRevisionGuard.ActiveUse([Activation("Active", new string('A', 64))], hash), "other revisions are irrelevant");
    }

    private static PluginActivation Activation(string state, string hash) =>
        new("activation-1", "sample.plugin", "1.0.0", hash, Scope, state, 1, null, [], false, null, "process");

    private static async Task<JsonDocument> Run(PluginUninstallTool tool, string id, string version, string hash)
    {
        var result = await tool.ExecuteAsync(new()
        {
            Context = new ToolExecutionContext { WorkspaceId = "workspace", SessionId = "session", AgentInstanceId = "agent" },
            ToolCallId = Guid.NewGuid().ToString("N"),
            ArgumentsJson = JsonSerializer.Serialize(new { id, version, contentHash = hash }),
        });
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output!);
    }

    private static void WritePackage(string directory, string id, string version)
    {
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        var manifest = JsonSerializer.Serialize(new
        {
            schema = "pudding-tool-plugin/v2", id, name = "Uninstall sample", version,
            description = "uninstall fixture",
            entry = new { assembly = "bin/" + id + ".dll", type = "Sample.Plugin" },
            requestedCapabilities = new[] { "state.read" },
            tools = new[]
            {
                new { id = "probe", name = "Probe", description = "fixture",
                    inputSchema = new { type = "object" }, outputSchema = new { type = "object" } },
            },
        });
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(directory, "bin", id + ".dll"), [1, 2, 3, 4]);
    }
}
