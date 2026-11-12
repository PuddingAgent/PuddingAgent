using System.Text;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class PluginRegisterDirectoryTests
{
    [TestMethod]
    public async Task RegisterFromDirectory_CopiesInsteadOfMoving_IsIdempotent_AndRejectsMissingEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-register-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PluginPackageStore(Path.Combine(root, "packages"));
            var source = Path.Combine(root, "src");
            WritePackage(source, "sample.dir", "1.0.0", entry: "bin/sample.dir.dll", createEntry: true);

            var package = await store.RegisterDirectoryAsync(source, default);
            Assert.AreEqual("sample.dir", package.Manifest.Id);
            Assert.IsTrue(Directory.Exists(package.Directory), "package content must land in the store");
            Assert.IsTrue(File.Exists(Path.Combine(source, "plugin.json")), "registration copies; the source directory must stay intact");

            // 同一目录重复注册：内容哈希不变，仓库不出现第二份
            var again = await store.RegisterDirectoryAsync(source, default);
            Assert.AreEqual(package.ContentHash, again.ContentHash);
            Assert.AreEqual(1, store.Scan().Packages.Count);

            // 清单声明的入口不存在 → 拒绝，且不留下半成品
            var broken = Path.Combine(root, "broken");
            WritePackage(broken, "sample.broken", "1.0.0", entry: "bin/absent.dll", createEntry: false);
            await Assert.ThrowsExactlyAsync<PluginException>(() => store.RegisterDirectoryAsync(broken, default));
            Assert.AreEqual(1, store.Scan().Packages.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Remove_DropsOnlyTheExactRevision_AndReportsMissingRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-remove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PluginPackageStore(Path.Combine(root, "packages"));
            var source = Path.Combine(root, "src");
            WritePackage(source, "sample.remove", "1.0.0", entry: "bin/sample.remove.dll", createEntry: true);
            var package = await store.RegisterDirectoryAsync(source, default);
            Assert.AreEqual(1, store.Scan().Packages.Count);

            Assert.IsTrue(store.Remove(package.Manifest.Id, package.Manifest.Version, package.ContentHash));
            Assert.AreEqual(0, store.Scan().Packages.Count);
            Assert.IsFalse(Directory.Exists(package.Directory), "removed revision must be gone from disk");

            // 幂等：已不存在的修订再次删除返回 false，而不是抛错
            Assert.IsFalse(store.Remove(package.Manifest.Id, package.Manifest.Version, package.ContentHash));

            // 非法哈希被拒，不当作路径拼接
            Assert.ThrowsExactly<PluginException>(() => store.Remove(package.Manifest.Id, package.Manifest.Version, ".."));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WritePackage(string directory, string id, string version, string entry, bool createEntry)
    {
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        var manifest = JsonSerializer.Serialize(new
        {
            schema = "pudding-tool-plugin/v2", id, name = "Directory sample", version,
            description = "register-from-directory fixture",
            entry = new { assembly = entry, type = "Sample.Plugin" },
            requestedCapabilities = new[] { "state.read" },
            tools = new[]
            {
                new { id = "probe", name = "Probe", description = "fixture",
                    inputSchema = new { type = "object" }, outputSchema = new { type = "object" } },
            },
        });
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest, new UTF8Encoding(false));
        if (createEntry) File.WriteAllBytes(Path.Combine(directory, entry), [1, 2, 3, 4]);
    }
}
