using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class GlobalAgentTemplateApiControllerTests
{
    [TestMethod]
    public async Task CreateAndGet_ShouldUseFileBackedTemplateStore()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var service = CreateFileService(temp.Path, options);
        var controller = new GlobalAgentTemplateApiController(service);

        var request = CreateRequest(
            templateId: "api-template",
            name: "API Template",
            selectedCapabilityIds: ["cap-http-fetch", "cap-python"],
            selectedSkillPackageIds: ["skill-a"]);

        var createResult = await controller.Create(request, CancellationToken.None);

        var createdAt = Assert.IsInstanceOfType<CreatedAtActionResult>(createResult.Result);
        var created = Assert.IsInstanceOfType<GlobalAgentTemplateDto>(createdAt.Value);
        Assert.AreEqual("api-template", created.TemplateId);
        CollectionAssert.AreEqual(new[] { "cap-http-fetch", "cap-python" }, created.SelectedCapabilityIds);
        CollectionAssert.AreEqual(new[] { "skill-a" }, created.SelectedSkillPackageIds);

        await using (var db = new PlatformDbContext(options))
        {
            Assert.AreEqual(0, await db.GlobalAgentTemplates.CountAsync());
        }

        var getResult = await controller.Get("api-template", CancellationToken.None);
        var getOk = Assert.IsInstanceOfType<OkObjectResult>(getResult.Result);
        var fetched = Assert.IsInstanceOfType<GlobalAgentTemplateDto>(getOk.Value);
        Assert.AreEqual("system prompt", fetched.SystemPrompt);
        Assert.AreEqual("agents prompt", fetched.AgentsPrompt);
        Assert.AreEqual(10, fetched.SortOrder);
    }

    [TestMethod]
    public async Task UpdateAndDelete_ShouldRoundTripThroughFileBackedTemplateStore()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var service = CreateFileService(temp.Path, options);
        var controller = new GlobalAgentTemplateApiController(service);

        await controller.Create(
            CreateRequest("api-template", "API Template", ["cap-http-fetch"], []),
            CancellationToken.None);

        var updateResult = await controller.Update(
            "api-template",
            CreateRequest("api-template", "Updated API Template", ["cap-python"], ["skill-b"]),
            CancellationToken.None);

        var updateOk = Assert.IsInstanceOfType<OkObjectResult>(updateResult.Result);
        var updated = Assert.IsInstanceOfType<GlobalAgentTemplateDto>(updateOk.Value);
        Assert.AreEqual("Updated API Template", updated.Name);
        CollectionAssert.AreEqual(new[] { "cap-python" }, updated.SelectedCapabilityIds);
        CollectionAssert.AreEqual(new[] { "skill-b" }, updated.SelectedSkillPackageIds);

        var deleteResult = await controller.Delete("api-template", CancellationToken.None);
        Assert.IsInstanceOfType<NoContentResult>(deleteResult);

        var missingResult = await controller.Get("api-template", CancellationToken.None);
        Assert.IsInstanceOfType<NotFoundResult>(missingResult.Result);
    }

    [TestMethod]
    public async Task CreateDuplicate_ShouldReturnConflict()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var service = CreateFileService(temp.Path, options);
        var controller = new GlobalAgentTemplateApiController(service);

        var request = CreateRequest(
            templateId: "api-template",
            name: "API Template",
            selectedCapabilityIds: ["cap-http-fetch"],
            selectedSkillPackageIds: []);

        var first = await controller.Create(request, CancellationToken.None);
        Assert.IsInstanceOfType<CreatedAtActionResult>(first.Result);

        var second = await controller.Create(request, CancellationToken.None);
        var conflict = Assert.IsInstanceOfType<ConflictObjectResult>(second.Result);
        Assert.IsNotNull(conflict.Value);
    }

    private static async Task<DbContextOptions<PlatformDbContext>> CreateDatabaseAsync(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var db = new PlatformDbContext(options);
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
        return options;
    }

    private static AgentTemplateFileService CreateFileService(
        string root,
        DbContextOptions<PlatformDbContext> options)
    {
        var dbFactory = new TestDbContextFactory(options);
        var avatarCatalog = new AgentAvatarCatalog(dbFactory, NullLogger<AgentAvatarCatalog>.Instance);
        return new AgentTemplateFileService(
            PuddingDataPaths.FromRoot(root),
            avatarCatalog,
            NullLogger<AgentTemplateFileService>.Instance);
    }

    private static UpsertGlobalAgentTemplateRequest CreateRequest(
        string templateId,
        string name,
        List<string> selectedCapabilityIds,
        List<string> selectedSkillPackageIds) =>
        new(
            TemplateId: templateId,
            Name: name,
            Description: "template description",
            Role: "Service",
            SystemPrompt: "system prompt",
            UserPromptTemplate: "{{input}}",
            PreferredProviderId: "mimo",
            PreferredModelId: "mimo-v2.5-pro",
            MaxContextTokens: 8192,
            MaxReplyTokens: 2048,
            ContainerImage: "docker.xuanyuan.run/library/ubuntu:latest",
            SelectedCapabilityIds: selectedCapabilityIds,
            SelectedSkillPackageIds: selectedSkillPackageIds,
            IsEnabled: true,
            SortOrder: 10,
            PersonaPrompt: "persona prompt",
            ToolsDescription: "tools description",
            BootstrapTemplate: "bootstrap template",
            AvatarId: "avatar-neutral",
            MemoryLlmProviderId: "mimo",
            MemoryLlmModelId: "mimo-v2.5",
            MemorySearchMode: "deep",
            ReasoningEffort: "high",
            MaxRounds: 321,
            MaxElapsedSeconds: 654,
            MaxToolCallsTotal: 42,
            ConsciousProfileId: "profile.conscious",
            SubconsciousProfileId: "profile.subconscious",
            AgentsPrompt: "agents prompt",
            MemoryPrompt: "memory prompt");

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-agent-template-api-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
