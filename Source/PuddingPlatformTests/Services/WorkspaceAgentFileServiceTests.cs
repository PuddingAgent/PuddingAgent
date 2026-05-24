using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class WorkspaceAgentFileServiceTests
{
    [TestMethod]
    public async Task CreateAgentAsync_ShouldBeVisibleInListAgentsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-workspace-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = PuddingDataPaths.FromRoot(root);
            var dbPath = Path.Combine(root, "platform.db");
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.AgentAvatars.Add(new AgentAvatarEntity
                {
                    AvatarId = "avatar-neutral",
                    Name = "Neutral",
                    FileName = "agent-avatar-neutral.png",
                    UrlPath = "/assets/agent-avatars/agent-avatar-neutral.png",
                    VisualTraitsJson = "[]",
                    IsBuiltIn = true,
                    IsEnabled = true,
                    SortOrder = 1,
                });
                await db.SaveChangesAsync();
            }

            var dbFactory = new TestDbContextFactory(options);
            var avatarCatalog = new AgentAvatarCatalog(dbFactory, NullLogger<AgentAvatarCatalog>.Instance);
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                NullLogger<WorkspaceAgentFileService>.Instance);

            var created = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "test01",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: null,
                    SystemPromptOverride: null,
                    PreferredProviderId: "mimo",
                    PreferredModelId: "mimo-v2.5-pro"));

            var listed = await service.ListAgentsAsync("default");

            Assert.HasCount(1, listed);
            Assert.AreEqual(created.AgentId, listed[0].AgentId);
            Assert.AreEqual("test01", listed[0].Name);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
