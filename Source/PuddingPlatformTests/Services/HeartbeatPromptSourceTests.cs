using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 心跳提示词来源测试：验证 heartbeatPrompt.md（实例级 + embedded 全局默认）是
/// 心跳提示词的唯一来源。
///
/// 覆盖任务验收：① 心跳提示词从 heartbeatPrompt.md 读取；② 文件缺失时回退默认
/// 来源（embedded heartbeatPrompt.md → 编译期常量）且不返回空提示词。
/// </summary>
[TestClass]
public sealed class HeartbeatPromptSourceTests
{
    [TestMethod]
    public void EmbeddedDefaultHeartbeatPrompt_ShouldBeAvailableAndNonEmpty()
    {
        var prompt = WorkspaceAgentFileService.TryReadDefaultHeartbeatPrompt();

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(prompt),
            "embedded heartbeatPrompt.md 资源应存在且非空（唯一权威默认来源）");
        Assert.IsTrue(prompt!.Contains("[系统心跳]"), "默认心跳提示词应包含系统心跳标题");
    }

    [TestMethod]
    public async Task GetAgentHeartbeatPromptAsync_WhenInstanceFileExists_ReturnsFileContent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-heartbeat-prompt-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = PuddingDataPaths.FromRoot(root);
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
                    Name: "heartbeat-test",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: null,
                    SystemPromptOverride: null,
                    PreferredProviderId: "mimo",
                    PreferredModelId: "mimo-v2.5-pro"));

            // 覆写实例 heartbeatPrompt.md，验证运行时读取的是该文件内容。
            const string customPrompt = "[系统心跳]\n自定义心跳提示词：检查目标推进。";
            await File.WriteAllTextAsync(
                Path.Combine(paths.AgentInstanceRoot(created.AgentId), "heartbeatPrompt.md"),
                customPrompt);

            var prompt = await service.GetAgentHeartbeatPromptAsync("default", created.AgentId);

            Assert.AreEqual(customPrompt, prompt, "实例 heartbeatPrompt.md 存在时应以其内容为准");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetAgentHeartbeatPromptAsync_WhenInstanceFileMissing_SeedsDefaultAndReturns()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-heartbeat-prompt-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = PuddingDataPaths.FromRoot(root);
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
                    Name: "heartbeat-test",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: null,
                    SystemPromptOverride: null,
                    PreferredProviderId: "mimo",
                    PreferredModelId: "mimo-v2.5-pro"));

            var heartbeatPath = Path.Combine(
                paths.AgentInstanceRoot(created.AgentId),
                "heartbeatPrompt.md");
            File.Delete(heartbeatPath);

            var prompt = await service.GetAgentHeartbeatPromptAsync("default", created.AgentId);

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(prompt),
                "实例 heartbeatPrompt.md 缺失时应回退默认来源，不能返回空提示词");
            Assert.IsTrue(
                File.Exists(heartbeatPath),
                "实例 heartbeatPrompt.md 缺失时应重新种子化，保证后续读取有文件可依");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static IServiceScopeFactory CreateMemoryScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryLibraryAdminService>(new RecordingMemoryLibraryAdminService());
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class RecordingMemoryLibraryAdminService : IMemoryLibraryAdminService
    {
        public Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryRecord>> GetLibrariesAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LibraryRecord> EnsureDefaultLibraryAsync(string workspaceId, string agentId, CancellationToken ct = default)
            => Task.FromResult(new LibraryRecord(
                LibraryId: $"library-{agentId}",
                WorkspaceId: workspaceId,
                Name: "默认记忆图书馆",
                Description: "Agent 专属记忆图书馆",
                CreatedAt: 1,
                UpdatedAt: 1,
                AgentId: agentId));

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
