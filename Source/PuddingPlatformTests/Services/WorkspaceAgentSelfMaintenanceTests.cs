using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class WorkspaceAgentSelfMaintenanceTests
{
    [TestMethod]
    public async Task InspectAsync_ReportsCanonicalAgentDocumentsAsHealthy()
    {
        using var fixture = new SelfMaintenanceFixture();
        await fixture.SeedAgentAsync(canonicalReferences: true);

        var snapshot = await fixture.Service.InspectAsync("agent-a");

        Assert.IsTrue(snapshot.IsHealthy, string.Join("; ", snapshot.Issues));
        Assert.AreEqual("agent-a", snapshot.AgentInstanceId);
        Assert.HasCount(6, snapshot.Documents);
        Assert.IsTrue(snapshot.Documents.All(document =>
            document.Exists
            && document.ReferencedByManifest
            && !string.IsNullOrWhiteSpace(document.Sha256)));
    }

    [TestMethod]
    public async Task UpdateDocumentAsync_WritesCanonicalRootFileAndRepairsManifestReference()
    {
        using var fixture = new SelfMaintenanceFixture();
        await fixture.SeedAgentAsync(canonicalReferences: false);

        var result = await fixture.Service.UpdateDocumentAsync(
            "agent-a",
            AgentSelfStateDocuments.Memory,
            "# Updated memory");

        Assert.IsTrue(result.ManifestReferenceRepaired);
        Assert.AreEqual("# Updated memory", await File.ReadAllTextAsync(
            Path.Combine(fixture.Paths.AgentInstanceRoot("agent-a"), "MEMORY.md")));
        Assert.IsFalse(Directory.Exists(
            Path.Combine(fixture.Paths.AgentInstanceRoot("agent-a"), "persona")));

        var manifest = await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(
            Path.Combine(fixture.Paths.AgentInstanceRoot("agent-a"), "manifest.json"));
        Assert.IsNotNull(manifest);
        Assert.AreEqual("MEMORY.md", manifest!.MemoryMdFile);
    }

    [TestMethod]
    public async Task UpdateDocumentAsync_StaleHashDoesNotOverwriteDocument()
    {
        using var fixture = new SelfMaintenanceFixture();
        await fixture.SeedAgentAsync(canonicalReferences: true);
        var before = await fixture.Service.ReadDocumentAsync("agent-a", AgentSelfStateDocuments.Soul);

        var error = await Assert.ThrowsExactlyAsync<AgentSelfStateConflictException>(
            () => fixture.Service.UpdateDocumentAsync(
                "agent-a",
                AgentSelfStateDocuments.Soul,
                "replacement",
                expectedSha256: "stale"));

        Assert.AreEqual("stale", error.ExpectedSha256);
        var after = await fixture.Service.ReadDocumentAsync("agent-a", AgentSelfStateDocuments.Soul);
        Assert.AreEqual(before.Content, after.Content);
        Assert.AreEqual(before.Sha256, after.Sha256);
    }

    [TestMethod]
    public async Task InspectAsync_RejectsAgentIdPathTraversal()
    {
        using var fixture = new SelfMaintenanceFixture();

        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => fixture.Service.InspectAsync("../agent-b"));

        StringAssert.Contains(error.Message, "Agent instance ID is invalid");
    }

    private sealed class SelfMaintenanceFixture : IDisposable
    {
        private readonly AvatarCatalogTestFixture _avatarFixture = new();

        public SelfMaintenanceFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "pudding-agent-self-maintenance-tests",
                Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
            Directory.CreateDirectory(Paths.AgentInstancesRoot);

            var provider = new ServiceCollection().BuildServiceProvider();
            var templateService = new AgentTemplateFileService(
                Paths,
                _avatarFixture.Catalog,
                NullLogger<AgentTemplateFileService>.Instance);
            Service = new WorkspaceAgentFileService(
                Paths,
                templateService,
                _avatarFixture.Catalog,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkspaceAgentFileService>.Instance);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }
        public WorkspaceAgentFileService Service { get; }

        public async Task SeedAgentAsync(bool canonicalReferences)
        {
            var root = Paths.AgentInstanceRoot("agent-a");
            Directory.CreateDirectory(root);
            var manifest = new AgentInstanceManifest
            {
                AgentInstanceId = "agent-a",
                TemplateId = "general-assistant",
                WorkspaceId = "default",
                DisplayName = "Agent A",
                SoulMdFile = "SOUL.md",
                AgentsMdFile = "AGENTS.md",
                ToolsMdFile = "TOOLS.md",
                BootstrapMdFile = "BOOTSTRAP.md",
                MemoryMdFile = canonicalReferences ? "MEMORY.md" : null,
                HeartbeatMdFile = "heartbeatPrompt.md",
            };
            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(root, "manifest.json"),
                manifest);

            foreach (var fileName in new[]
                     {
                         "SOUL.md",
                         "AGENTS.md",
                         "TOOLS.md",
                         "BOOTSTRAP.md",
                         "MEMORY.md",
                         "heartbeatPrompt.md",
                     })
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, fileName),
                    $"# {fileName}");
            }
        }

        public void Dispose()
        {
            _avatarFixture.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
