using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Agents;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-02 I2 archive adapter: archive flips the terminal flags on the existing manifest (no second store), is fenced by
/// the manifest revision, and a stale revision is rejected without mutation.
/// </summary>
[TestClass]
public sealed class WorkspaceAgentAuthoringBackendTests
{
    [TestMethod]
    public async Task Archive_FlipsManifestFlags_AndRejectsStaleRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-agent-archive", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            const string agentId = "agent-1";
            await WriteAgentAsync(paths, agentId);
            using var avatar = new AvatarCatalogTestFixture();
            var services = new ServiceCollection().BuildServiceProvider();
            var fileService = new WorkspaceAgentFileService(paths, null!, avatar.Catalog,
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkspaceAgentFileService>.Instance);
            var backend = new WorkspaceAgentAuthoringBackend(fileService, paths);
            var manifestPath = Path.Combine(paths.AgentInstanceRoot(agentId), "manifest.json");
            var revision = File.GetLastWriteTimeUtc(manifestPath).Ticks;

            var receipt = await backend.ArchiveAsync("default", agentId, revision);
            Assert.AreEqual(agentId, receipt.AgentId);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<AgentInstanceManifest>(
                await File.ReadAllTextAsync(manifestPath),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            Assert.IsTrue(manifest.IsFrozen);
            Assert.IsFalse(manifest.IsEnabled);

            var stale = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await backend.ArchiveAsync("default", agentId, revision));
            StringAssert.Contains(stale.Message, "revision");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WriteAgentAsync(PuddingDataPaths paths, string agentId)
    {
        var dir = paths.AgentInstanceRoot(agentId);
        Directory.CreateDirectory(dir);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(new AgentInstanceManifest
            {
                AgentInstanceId = agentId,
                WorkspaceId = "default",
                TemplateId = "general-assistant",
                DisplayName = "Archivable",
            }, json));
        var refDir = Path.Combine(paths.WorkspaceRoot("default"), "agents", agentId);
        Directory.CreateDirectory(refDir);
        await File.WriteAllTextAsync(Path.Combine(refDir, "ref.json"),
            System.Text.Json.JsonSerializer.Serialize(new WorkspaceAgentRef
            {
                AgentInstanceId = agentId,
                WorkspaceId = "default",
                AgentPath = Path.GetRelativePath(refDir, dir),
                IsEnabled = true,
            }, json));
    }
}
