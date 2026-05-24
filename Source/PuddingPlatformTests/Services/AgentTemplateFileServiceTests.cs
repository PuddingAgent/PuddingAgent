using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentTemplateFileServiceTests
{
    [TestMethod]
    public async Task TemplateRoundTrip_ShouldPreserveAllEditableGlobalTemplateFields()
    {
        using var temp = TemporaryDirectory.Create();
        var service = await CreateServiceAsync(temp.Path);

        var request = CreateRequest(
            preferredProviderId: "mimo",
            preferredModelId: "mimo-v2.5-pro",
            memoryLlmProviderId: "mimo",
            memoryLlmModelId: "mimo-v2.5");

        await service.CreateTemplateAsync(request);

        var saved = await service.GetTemplateAsync("assistant");

        Assert.IsNotNull(saved);
        Assert.AreEqual("Assistant", saved.Name);
        Assert.AreEqual("system prompt", saved.SystemPrompt);
        Assert.AreEqual("{{input}}", saved.UserPromptTemplate);
        Assert.AreEqual("persona prompt", saved.PersonaPrompt);
        Assert.AreEqual("tools description", saved.ToolsDescription);
        Assert.AreEqual("bootstrap template", saved.BootstrapTemplate);
        Assert.AreEqual("agents prompt", saved.AgentsPrompt);
        Assert.AreEqual("memory prompt", saved.MemoryPrompt);
        Assert.AreEqual("mimo", saved.PreferredProviderId);
        Assert.AreEqual("mimo-v2.5-pro", saved.PreferredModelId);
        Assert.AreEqual("mimo", saved.MemoryLlmProviderId);
        Assert.AreEqual("mimo-v2.5", saved.MemoryLlmModelId);
        Assert.AreEqual("deep", saved.MemorySearchMode);
        Assert.AreEqual("high", saved.ReasoningEffort);
        Assert.AreEqual(8192, saved.MaxContextTokens);
        Assert.AreEqual(2048, saved.MaxReplyTokens);
        Assert.AreEqual(321, saved.MaxRounds);
        Assert.AreEqual(654, saved.MaxElapsedSeconds);
        Assert.AreEqual(42, saved.MaxToolCallsTotal);
        Assert.AreEqual("docker.xuanyuan.run/library/ubuntu:latest", saved.ContainerImage);
        Assert.AreEqual(10, saved.SortOrder);
        CollectionAssert.AreEqual(new[] { "cap-http-fetch", "cap-python" }, saved.SelectedCapabilityIds);
        CollectionAssert.AreEqual(new[] { "skill-a", "skill-b" }, saved.SelectedSkillPackageIds);
        Assert.AreEqual("profile.conscious", saved.ConsciousProfileId);
        Assert.AreEqual("profile.subconscious", saved.SubconsciousProfileId);
        Assert.AreEqual("avatar-neutral", saved.AvatarId);
    }

    [TestMethod]
    public async Task CreateThenUpdate_ShouldPersistManifestTimestamps()
    {
        using var temp = TemporaryDirectory.Create();
        var service = await CreateServiceAsync(temp.Path);

        var created = await service.CreateTemplateAsync(CreateRequest("mimo", "mimo-v2.5-pro", "mimo", "mimo-v2.5"));
        var createdAt = created.CreatedAt;
        var createdUpdatedAt = created.UpdatedAt;

        Assert.AreEqual(createdAt, createdUpdatedAt);

        await Task.Delay(20);

        var updated = await service.UpdateTemplateAsync(
            "assistant",
            CreateRequest("mimo", "mimo-v2.5-pro", "mimo", "mimo-v2.5") with { Name = "Assistant v2" });

        Assert.AreEqual(createdAt, updated.CreatedAt);
        Assert.IsTrue(updated.UpdatedAt >= createdUpdatedAt);
        Assert.AreEqual("Assistant v2", updated.Name);
    }

    [TestMethod]
    public async Task InvalidAvatarId_ShouldFallbackToDefaultAvatarAndLogWarning()
    {
        using var temp = TemporaryDirectory.Create();
        var logger = new TestLogger<AgentTemplateFileService>();
        var service = await CreateServiceAsync(temp.Path, logger);

        await service.CreateTemplateAsync(
            CreateRequest("mimo", "mimo-v2.5-pro", "mimo", "mimo-v2.5") with { AvatarId = "avatar-missing" });

        var saved = await service.GetTemplateAsync("assistant");

        Assert.IsNotNull(saved);
        Assert.AreEqual("avatar-neutral", saved.AvatarId);
        Assert.AreEqual("/assets/agent-avatars/agent-avatar-neutral.png", saved.AvatarUrl);
        Assert.IsTrue(logger.WarningMessages.Any(m => m.Contains("avatar-missing", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task LegacyManifestWithoutTimestamps_ShouldUseStableFileTimes()
    {
        using var temp = TemporaryDirectory.Create();
        var service = await CreateServiceAsync(temp.Path);
        var templateDir = PuddingDataPaths.FromRoot(temp.Path).AgentTemplateRoot("legacy");
        Directory.CreateDirectory(templateDir);
        var manifestPath = Path.Combine(templateDir, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "templateId": "legacy",
              "name": "Legacy",
              "role": "Service",
              "maxContextTokens": 8192,
              "maxReplyTokens": 2048,
              "isEnabled": true
            }
            """);
        var createdAt = new DateTime(2026, 5, 24, 1, 2, 3, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 5, 24, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(manifestPath, createdAt);
        File.SetLastWriteTimeUtc(manifestPath, updatedAt);

        var first = await service.GetTemplateAsync("legacy");
        await Task.Delay(20);
        var second = await service.GetTemplateAsync("legacy");

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first.CreatedAt, second.CreatedAt);
        Assert.AreEqual(first.UpdatedAt, second.UpdatedAt);
        Assert.AreEqual(new DateTimeOffset(createdAt), first.CreatedAt);
        Assert.AreEqual(new DateTimeOffset(updatedAt), first.UpdatedAt);
    }

    private static async Task<AgentTemplateFileService> CreateServiceAsync(
        string root,
        ILogger<AgentTemplateFileService>? logger = null)
    {
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
        return new AgentTemplateFileService(
            PuddingDataPaths.FromRoot(root),
            avatarCatalog,
            logger ?? NullLogger<AgentTemplateFileService>.Instance);
    }

    private static UpsertGlobalAgentTemplateRequest CreateRequest(
        string? preferredProviderId,
        string? preferredModelId,
        string? memoryLlmProviderId,
        string? memoryLlmModelId) =>
        new(
            TemplateId: "assistant",
            Name: "Assistant",
            Description: "template description",
            Role: "Service",
            SystemPrompt: "system prompt",
            UserPromptTemplate: "{{input}}",
            PreferredProviderId: preferredProviderId,
            PreferredModelId: preferredModelId,
            MaxContextTokens: 8192,
            MaxReplyTokens: 2048,
            ContainerImage: "docker.xuanyuan.run/library/ubuntu:latest",
            SelectedCapabilityIds: ["cap-http-fetch", "cap-python"],
            SelectedSkillPackageIds: ["skill-a", "skill-b"],
            IsEnabled: true,
            SortOrder: 10,
            PersonaPrompt: "persona prompt",
            ToolsDescription: "tools description",
            BootstrapTemplate: "bootstrap template",
            AvatarId: "avatar-neutral",
            MemoryLlmProviderId: memoryLlmProviderId,
            MemoryLlmModelId: memoryLlmModelId,
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
                "pudding-agent-template-tests",
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

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> WarningMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            WarningMessages.Add(formatter(state, exception));
        }
    }
}
