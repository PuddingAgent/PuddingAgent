using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "general-assistant",
                Name: "General Assistant",
                Description: null,
                Role: "Service",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "template-provider",
                PreferredModelId: "template-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 10));

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
            Assert.AreEqual("template-provider", listed[0].PreferredProviderId);
            Assert.AreEqual("template-model", listed[0].PreferredModelId);

            var loaded = await service.GetAgentAsync("default", created.AgentId);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("template-provider", loaded!.PreferredProviderId);
            Assert.AreEqual("template-model", loaded.PreferredModelId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetAndListAgentsAsync_ShouldIgnoreLegacyInstanceLlmConfigAndUseTemplateDefaults()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "general-assistant",
                Name: "General Assistant",
                Description: null,
                Role: "Service",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "deepseek",
                PreferredModelId: "deepseek-v4-pro",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 10));

            var created = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "default assistant",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:general-assistant",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));

            Directory.CreateDirectory(paths.AgentInstanceConfigRoot(created.AgentId));
            await File.WriteAllTextAsync(
                paths.AgentInstanceConfigFile(created.AgentId, "llm.json"),
                """
                {
                  "conscious": {
                    "providerId": "legacy-provider",
                    "modelId": "legacy-model"
                  }
                }
                """);

            var loaded = await service.GetAgentAsync("default", created.AgentId);
            var listed = await service.ListAgentsAsync("default");

            Assert.IsNotNull(loaded);
            Assert.AreEqual("deepseek", loaded!.PreferredProviderId);
            Assert.AreEqual("deepseek-v4-pro", loaded.PreferredModelId);
            Assert.HasCount(1, listed);
            Assert.AreEqual("deepseek", listed[0].PreferredProviderId);
            Assert.AreEqual("deepseek-v4-pro", listed[0].PreferredModelId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SetAgentMainSessionAsync_ShouldPersistBindingInAgentManifest()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            var created = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "main-bound-agent",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: null,
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));

            var updated = await service.SetAgentMainSessionAsync("default", created.AgentId, "main-session-123");
            var loaded = await service.GetAgentAsync("default", created.AgentId);
            var listed = await service.ListAgentsAsync("default");

            Assert.AreEqual("main-session-123", updated.MainSessionId);
            Assert.AreEqual("main-session-123", loaded!.MainSessionId);
            Assert.AreEqual("main-session-123", listed.Single().MainSessionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAgentAsync_ShouldEnsureDefaultMemoryLibrary()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var memoryAdmin = new RecordingMemoryLibraryAdminService();
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(memoryAdmin),
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
                    PreferredProviderId: null,
                    PreferredModelId: null));

            Assert.HasCount(1, memoryAdmin.EnsureDefaultCalls);
            Assert.AreEqual("default", memoryAdmin.EnsureDefaultCalls[0].WorkspaceId);
            Assert.AreEqual(created.AgentId, memoryAdmin.EnsureDefaultCalls[0].AgentId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAgentAsync_ShouldResolveAvatarFromGlobalTemplatePrefix()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "zixun",
                Name: "咨询专家",
                Description: null,
                Role: "Service",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: null,
                PreferredModelId: null,
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 10,
                AvatarId: "smile"));

            var created = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "咨询专家",
                    Description: null,
                    DisplayName: "咨询专家",
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:zixun",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));

            var listed = await service.ListAgentsAsync("default");

            Assert.AreEqual("smile", created.AvatarId);
            Assert.AreEqual("/assets/agent-avatars/agent-avatar-smile.png", created.AvatarUrl);
            Assert.HasCount(1, listed);
            Assert.AreEqual("smile", listed[0].AvatarId);
            Assert.AreEqual("/assets/agent-avatars/agent-avatar-smile.png", listed[0].AvatarUrl);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FindFirstEnabledAuditAgentAsync_ShouldReturn_Audit_Agent_Profile_From_Template()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "aaa-service",
                Name: "Service Agent",
                Description: null,
                Role: "Service",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "service-provider",
                PreferredModelId: "service-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 10,
                ConsciousProfileId: "service.default"));
            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "zzz-audit",
                Name: "Audit Agent",
                Description: null,
                Role: "Audit",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "audit-provider",
                PreferredModelId: "audit-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 20,
                ConsciousProfileId: "audit.default"));

            await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "service",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:aaa-service",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));
            var audit = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "audit",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:zzz-audit",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));

            var profile = await service.FindFirstEnabledAuditAgentAsync("default");

            Assert.IsNotNull(profile);
            Assert.AreEqual("default", profile!.WorkspaceId);
            Assert.AreEqual(audit.AgentId, profile.AgentInstanceId);
            Assert.AreEqual("zzz-audit", profile.AgentTemplateId);
            Assert.AreEqual("audit.default", profile.ProfileId);
            Assert.AreEqual("audit-provider", profile.ProviderId);
            Assert.AreEqual("audit-model", profile.ModelId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAgentAsync_ShouldReject_Second_Audit_Agent_In_Workspace()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "audit-one",
                Name: "Audit One",
                Description: null,
                Role: "Audit",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "audit-provider",
                PreferredModelId: "audit-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 10));
            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "audit-two",
                Name: "Audit Two",
                Description: null,
                Role: "Audit",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "audit-provider",
                PreferredModelId: "audit-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 20));

            var first = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "audit one",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:audit-one",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));

            try
            {
                await service.CreateAgentAsync(
                    "default",
                    new CreateWorkspaceAgentRequest(
                        Name: "audit two",
                        Description: null,
                        DisplayName: null,
                        AvatarId: null,
                        AvatarUrl: null,
                        SourceTemplateId: "global:audit-two",
                        SystemPromptOverride: null,
                        PreferredProviderId: null,
                        PreferredModelId: null));
                Assert.Fail("Expected WorkspaceAuditAgentConflictException.");
            }
            catch (WorkspaceAuditAgentConflictException ex)
            {
                StringAssert.Contains(ex.Message, "当前工作空间已存在审计类型的agent");
                Assert.AreEqual(first.AgentId, ex.ExistingAgentId);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UpdateAgentAsync_ShouldReject_Changing_Another_Agent_To_Audit_When_Workspace_Has_Audit()
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
            }

            using var avatarFixture = new AvatarCatalogTestFixture();
            var avatarCatalog = avatarFixture.Catalog;
            var templateService = new AgentTemplateFileService(
                paths,
                avatarCatalog,
                NullLogger<AgentTemplateFileService>.Instance);
            var service = new WorkspaceAgentFileService(
                paths,
                templateService,
                avatarCatalog,
                CreateMemoryScopeFactory(),
                NullLogger<WorkspaceAgentFileService>.Instance);

            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "service-one",
                Name: "Service One",
                Description: null,
                Role: "Service",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: null,
                PreferredModelId: null,
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 10));
            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "audit-one",
                Name: "Audit One",
                Description: null,
                Role: "Audit",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "audit-provider",
                PreferredModelId: "audit-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 20));
            await templateService.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                TemplateId: "audit-two",
                Name: "Audit Two",
                Description: null,
                Role: "Audit",
                SystemPrompt: null,
                UserPromptTemplate: null,
                PreferredProviderId: "audit-provider",
                PreferredModelId: "audit-model",
                MaxContextTokens: 8192,
                MaxReplyTokens: 2048,
                ContainerImage: null,
                SelectedCapabilityIds: [],
                SelectedSkillPackageIds: [],
                IsEnabled: true,
                SortOrder: 30));

            var audit = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "audit one",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:audit-one",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));
            var serviceAgent = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "service one",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "global:service-one",
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null));

            try
            {
                await service.UpdateAgentAsync(
                    "default",
                    serviceAgent.AgentId,
                    new UpdateWorkspaceAgentRequest(
                        Name: "service one",
                        Description: null,
                        DisplayName: null,
                        AvatarId: null,
                        AvatarUrl: null,
                        SourceTemplateId: "global:audit-two",
                        SystemPromptOverride: null,
                        PreferredProviderId: null,
                        PreferredModelId: null,
                        IsEnabled: true));
                Assert.Fail("Expected WorkspaceAuditAgentConflictException.");
            }
            catch (WorkspaceAuditAgentConflictException ex)
            {
                StringAssert.Contains(ex.Message, "当前工作空间已存在审计类型的agent");
                Assert.AreEqual(audit.AgentId, ex.ExistingAgentId);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static IServiceScopeFactory CreateMemoryScopeFactory(
        IMemoryLibraryAdminService? memoryAdmin = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(memoryAdmin ?? new RecordingMemoryLibraryAdminService());
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class RecordingMemoryLibraryAdminService : IMemoryLibraryAdminService
    {
        public List<(string WorkspaceId, string AgentId)> EnsureDefaultCalls { get; } = [];

        public Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryRecord>> GetLibrariesAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LibraryRecord> EnsureDefaultLibraryAsync(string workspaceId, string agentId, CancellationToken ct = default)
        {
            EnsureDefaultCalls.Add((workspaceId, agentId));
            return Task.FromResult(new LibraryRecord(
                LibraryId: $"library-{EnsureDefaultCalls.Count}",
                WorkspaceId: workspaceId,
                Name: "默认记忆图书馆",
                Description: "Agent 专属记忆图书馆",
                CreatedAt: 1,
                UpdatedAt: 1,
                AgentId: agentId));
        }

        public Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(string workspaceId, string agentId, string libraryId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryBookPageDto> GetBookPageAsync(string workspaceId, string agentId, string bookId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(string workspaceId, string agentId, string query, int topK, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SourceReferenceRecord>> GetSourcesAsync(string workspaceId, string agentId, string ownerType, string ownerId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryPointersDto> GetPointersAsync(string workspaceId, string agentId, string sourceType, string sourceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryLibraryTreeNodeDto> CreateTreeNodeAsync(string workspaceId, string agentId, CreateMemoryTreeNodeRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryBookPageDto> CreateBookAsync(string workspaceId, string agentId, CreateMemoryBookRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryBookPageDto> UpdateBookAsync(string workspaceId, string agentId, string bookId, UpdateMemoryBookRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryChapterSectionDto> CreateChapterAsync(string workspaceId, string agentId, CreateMemoryChapterRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryChapterSectionDto> UpdateChapterAsync(string workspaceId, string agentId, string chapterId, UpdateMemoryChapterRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ArchiveBookAsync(string workspaceId, string agentId, string bookId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ArchiveChapterAsync(string workspaceId, string agentId, string chapterId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
