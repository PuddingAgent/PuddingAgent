using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class PluginSearchToolTests
{
    [TestMethod]
    public async Task SearchMatchesIdentityTextAndCapability_AndReportsActiveSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new ExecutablePluginService(PuddingDataPaths.FromRoot(root));
            await Import(service, "sample.alpha", "Alpha reviewer", "1.0.0", "审查模型输出的样例插件", ["workspace.read"]);
            await Import(service, "sample.beta", "Beta scorer", "1.0.0", "unrelated helper", ["state.read"]);

            var tool = new PluginSearchTool(service);

            // 1. 命中描述文本（中文子串）
            var byDescription = await Search(tool, new { query = "样例" });
            Assert.AreEqual(1, byDescription.GetProperty("matched").GetInt32());
            Assert.AreEqual("sample.alpha", byDescription.GetProperty("items")[0].GetProperty("id").GetString());

            // 2. 命中能力名
            var byCapability = await Search(tool, new { query = "state.read" });
            Assert.AreEqual(1, byCapability.GetProperty("matched").GetInt32());
            Assert.AreEqual("sample.beta", byCapability.GetProperty("items")[0].GetProperty("id").GetString());

            // 3. 空查询列出全部；未挂载因此 active 为 false（检索不得谎报已安装）
            var all = await Search(tool, new { });
            Assert.AreEqual(2, all.GetProperty("matched").GetInt32());
            Assert.IsFalse(all.GetProperty("items")[0].GetProperty("active").GetBoolean());

            // 4. limit 生效且 matched 仍是真实命中数
            var limited = await Search(tool, new { limit = 1 });
            Assert.AreEqual(2, limited.GetProperty("matched").GetInt32());
            Assert.AreEqual(1, limited.GetProperty("returned").GetInt32());

            // 5. 无命中
            var none = await Search(tool, new { query = "no-such-plugin-xyz" });
            Assert.AreEqual(0, none.GetProperty("matched").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<JsonElement> Search(PluginSearchTool tool, object args)
    {
        var context = new ToolExecutionContext { WorkspaceId = "workspace", SessionId = "session", AgentInstanceId = "agent" };
        var result = await tool.ExecuteAsync(new()
        {
            Context = context,
            ToolCallId = Guid.NewGuid().ToString("N"),
            ArgumentsJson = JsonSerializer.Serialize(args),
        });
        Assert.IsTrue(result.Success, result.Error);
        using var document = JsonDocument.Parse(result.Output!);
        return document.RootElement.Clone();
    }

    private static async Task Import(ExecutablePluginService service, string id, string name, string version, string description, string[] capabilities)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                schema = "pudding-tool-plugin/v2", id, name, version, description,
                entry = new { assembly = "bin/" + id + ".dll", type = "Sample.Plugin" },
                requestedCapabilities = capabilities,
                tools = new[]
                {
                    new
                    {
                        id = "probe", name = "Probe", description = "Search fixture tool",
                        inputSchema = new { type = "object" },
                        outputSchema = new { type = "object" },
                    },
                },
            });
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open(), new UTF8Encoding(false)))
                await writer.WriteAsync(manifest);
            using var entry = zip.CreateEntry("bin/" + id + ".dll", CompressionLevel.NoCompression).Open();
            entry.Write([1, 2, 3, 4]);
        }
        buffer.Position = 0;
        await service.Packages.ImportAsync(buffer, default);
    }
}
