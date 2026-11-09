using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class PluginManifestOnlyTests
{
    [TestMethod]
    public void PluginManifestCatalog_Reads_Legacy_Metadata_Without_Contributing_Tools()
    {
        using var temp = new TempDirectory();
        WritePluginManifest(
            temp.Path,
            "pudding.code-search",
            """
            {
              "schema": "pudding-plugin/v1",
              "id": "pudding.code-search",
              "name": "Code Search Plugin",
              "version": "1.0.0",
              "entry": {
                "assembly": "bin/Pudding.Plugin.CodeSearch.dll",
                "type": "Pudding.Plugin.CodeSearch.CodeSearchPlugin"
              },
              "tools": [
                {
                  "id": "plugin_code_search",
                  "name": "Code search",
                  "description": "Search code symbols and files.",
                  "category": "Query",
                  "permissionLevel": "Low",
                  "safety": ["ReadOnly", "ConcurrencySafe"],
                  "enabledByDefault": false,
                  "sortOrder": 300,
                  "parameters": {
                    "properties": [
                      { "name": "query", "type": "string", "description": "Search keyword or symbol name." }
                    ],
                    "required": ["query"]
                  }
                }
              ],
              "permissions": {
                "filesystem": ["workspace-read"],
                "network": [],
                "shell": false
              },
              "compatibility": {
                "minHostVersion": "1.0.0",
                "targetFramework": "net10.0"
              }
            }
            """);

        var catalog = new PluginManifestCatalog(PuddingDataPaths.FromRoot(temp.Path));

        var plugins = catalog.ListPlugins();
        var descriptor = plugins.Single().Tools.Single();
        Assert.IsFalse(typeof(IPuddingToolSource).IsAssignableFrom(typeof(PluginManifestCatalog)));

        Assert.AreEqual(1, plugins.Count);
        Assert.AreEqual(PluginLoadStatus.ManifestOnly, plugins[0].Status);
        Assert.AreEqual("pudding.code-search", plugins[0].PluginId);
        Assert.AreEqual("plugin_code_search", descriptor.ToolId);
        Assert.AreEqual("Code search", descriptor.Name);
        Assert.AreEqual(ToolCategory.Query, descriptor.Category);
        Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly));
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.AreEqual("Plugin", descriptor.SourceKind);
        Assert.AreEqual("pudding.code-search", descriptor.SourceId);
        Assert.AreEqual("ManifestOnly", descriptor.RuntimeStatus);
        Assert.AreEqual("query", descriptor.Parameters.Properties.Single().Name);
        CollectionAssert.AreEqual(new[] { "query" }, descriptor.Parameters.Required.ToArray());
    }

    [TestMethod]
    public void PluginManifestCatalog_Rejects_Invalid_Tool_Ids()
    {
        using var temp = new TempDirectory();
        WritePluginManifest(
            temp.Path,
            "bad-plugin",
            """
            {
              "schema": "pudding-plugin/v1",
              "id": "bad-plugin",
              "name": "Bad Plugin",
              "version": "1.0.0",
              "entry": { "assembly": "bin/Bad.dll" },
              "tools": [
                {
                  "id": "plugin.bad_tool",
                  "name": "Bad tool",
                  "description": "Invalid dotted id.",
                  "category": "Query",
                  "permissionLevel": "Low",
                  "safety": ["ReadOnly"],
                  "parameters": { "properties": [], "required": [] }
                }
              ],
              "permissions": { "filesystem": [], "network": [], "shell": false },
              "compatibility": { "minHostVersion": "1.0.0", "targetFramework": "net10.0" }
            }
            """);

        var catalog = new PluginManifestCatalog(PuddingDataPaths.FromRoot(temp.Path));

        var plugin = catalog.ListPlugins().Single();

        Assert.AreEqual(PluginLoadStatus.ManifestInvalid, plugin.Status);
        StringAssert.Contains(plugin.StatusReason, "plugin.bad_tool");
        Assert.AreEqual(0, plugin.Tools.Count);
    }

    private static void WritePluginManifest(string dataRoot, string pluginId, string manifestJson)
    {
        var pluginRoot = Path.Combine(dataRoot, "plugins", pluginId);
        Directory.CreateDirectory(pluginRoot);
        File.WriteAllText(Path.Combine(pluginRoot, "plugin.json"), manifestJson);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-plugin-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test data; leaked temp files do not affect assertions.
            }
        }
    }
}
