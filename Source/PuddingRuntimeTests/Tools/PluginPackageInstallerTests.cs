using System.IO.Compression;
using System.Text;
using PuddingCode.Configuration;
using PuddingRuntime.Services.Plugins;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class PluginPackageInstallerTests
{
    [TestMethod]
    public async Task InstallAsync_ValidZip_InstallsManifestBeforeCatalogReload()
    {
        using var temp = new TempDirectory();
        await using var package = CreateZip(
            ("plugin.json", ValidManifest("pudding.uploaded")),
            ("bin/Pudding.Plugin.Uploaded.dll", "placeholder"));
        var installer = new PluginPackageInstaller(PuddingDataPaths.FromRoot(temp.Path));

        var result = await installer.InstallAsync(package, "uploaded.zip");

        Assert.AreEqual("pudding.uploaded", result.PluginId);
        Assert.IsFalse(result.RequiresRestart);
        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "plugins", "pudding.uploaded", "plugin.json")));
        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "plugins", "pudding.uploaded", "bin", "Pudding.Plugin.Uploaded.dll")));
    }

    [TestMethod]
    public async Task InstallAsync_PathTraversalEntry_RejectsBeforeExtraction()
    {
        using var temp = new TempDirectory();
        await using var package = CreateZip(
            ("plugin.json", ValidManifest("pudding.bad")),
            ("../evil.txt", "owned"));
        var installer = new PluginPackageInstaller(PuddingDataPaths.FromRoot(temp.Path));

        await Assert.ThrowsExactlyAsync<PluginPackageValidationException>(
            () => installer.InstallAsync(package, "bad.zip"));

        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "evil.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "plugins", "pudding.bad")));
    }

    private static MemoryStream CreateZip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static string ValidManifest(string pluginId) => $$"""
        {
          "schema": "pudding-plugin/v1",
          "id": "{{pluginId}}",
          "name": "Uploaded Plugin",
          "version": "1.0.0",
          "entry": { "assembly": "bin/Pudding.Plugin.Uploaded.dll" },
          "tools": [
            {
              "id": "uploaded_tool",
              "name": "Uploaded tool",
              "description": "Tool from uploaded plugin.",
              "category": "Query",
              "permissionLevel": "Low",
              "safety": ["ReadOnly"],
              "parameters": { "properties": [], "required": [] }
            }
          ]
        }
        """;

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-plugin-package-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
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
