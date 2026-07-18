using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class ChatApiControllerTemplateResolutionTests
{
    [TestMethod]
    public async Task ExplicitGlobalTemplate_ShouldIgnoreStaleWorkspaceTemplateWithSameCanonicalId()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var templateService = CreateFileService(temp.Path, options);

        await templateService.CreateTemplateAsync(CreateGlobalTemplateRequest());

        await using (var db = new PlatformDbContext(options))
        {
            var resolved = await InvokeResolveCapabilitiesAsync(
                db,
                templateService,
                "default",
                "global:general-assistant");

            Assert.IsNotNull(resolved.Policy);
            var policy = resolved.Policy!;
            Assert.IsTrue(policy.AllowShellExecution);
            CollectionAssert.Contains(policy.RequiresGrantToolNames.ToList(), "shell");

            Assert.IsNotNull(resolved.ToolDefinitions);
            var tools = resolved.ToolDefinitions!;
            CollectionAssert.Contains(tools.Select(t => t.Name).ToList(), "shell");
        }
    }

    [TestMethod]
    public async Task GlobalFileTemplate_ShouldResolveCapabilitiesFromToolRegistryWithoutDbCapabilityRows()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var templateService = CreateFileService(temp.Path, options);

        await templateService.CreateTemplateAsync(CreateGlobalTemplateRequest(
            selectedCapabilityIds: ["cap-file-patch"]));

        await using var db = new PlatformDbContext(options);
        var resolved = await InvokeResolveCapabilitiesAsync(
            db,
            templateService,
            "default",
            "global:general-assistant");

        Assert.IsNotNull(resolved.Policy);
        CollectionAssert.Contains(resolved.Policy!.RequiresGrantToolNames.ToList(), "file_patch");

        Assert.IsNotNull(resolved.ToolDefinitions);
        CollectionAssert.Contains(resolved.ToolDefinitions!.Select(t => t.Name).ToList(), "file_patch");
    }

    [TestMethod]
    public async Task ExplicitGlobalMemoryConfigMissing_ShouldFallbackToSubconsciousRoleConfig()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var templateService = CreateFileService(temp.Path, options);

        await templateService.CreateTemplateAsync(CreateGlobalTemplateRequest(
            preferredProviderId: "global-provider",
            preferredModelId: "global-model"));

        var resolver = new AgentLLMConfigResolver(
            templateService,
            new PuddingFileLlmConfigService(CreateLlmConfigWithSubconsciousRole()),
            NullLogger<AgentLLMConfigResolver>.Instance);

        var resolved = await resolver.ResolveMemoryAsync("global:general-assistant", "default");

        Assert.IsNotNull(resolved);
        Assert.IsNull(resolved.ProviderId);
        Assert.AreEqual("memory-model", resolved.ModelId);
        Assert.AreEqual("https://memory.example/v1", resolved.Endpoint);
        Assert.AreEqual("memory-key", resolved.ApiKey);
        Assert.AreEqual("deep", resolved.SearchMode);
    }

    [TestMethod]
    public async Task ResolveMemoryAsync_ShouldResolveProviderFromFileConfig()
    {
        using var temp = TemporaryDirectory.Create();
        var options = await CreateDatabaseAsync(temp.Path);
        var templateService = CreateFileService(temp.Path, options);
        var llmConfigService = new PuddingFileLlmConfigService(new PuddingLlmProvidersConfig
        {
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "file-provider",
                    Name = "File Provider",
                    BaseUrl = "https://file-provider.example/v1",
                    ApiKey = "file-key",
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "file-memory",
                            Name = "File Memory",
                            IsDefault = true,
                            SortOrder = 1,
                        },
                    ],
                },
            ],
        });

        await templateService.CreateTemplateAsync(CreateGlobalTemplateRequest(
            memoryLlmProviderId: "file-provider",
            memoryLlmModelId: "file-memory"));

        var resolver = new AgentLLMConfigResolver(
            templateService,
            llmConfigService,
            NullLogger<AgentLLMConfigResolver>.Instance);

        var resolved = await resolver.ResolveMemoryAsync("global:general-assistant", "default");

        Assert.IsNotNull(resolved);
        Assert.AreEqual("file-provider", resolved.ProviderId);
        Assert.AreEqual("file-memory", resolved.ModelId);
        Assert.AreEqual("https://file-provider.example/v1", resolved.Endpoint);
        Assert.AreEqual("file-key", resolved.ApiKey);
    }

    private static async Task<ResolvedCapabilitiesView> InvokeResolveCapabilitiesAsync(
        PlatformDbContext db,
        AgentTemplateFileService templateService,
        string workspaceId,
        string templateId)
    {
        var method = typeof(ChatApiController).GetMethod(
            "ResolveCapabilitiesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var task = (Task)method.Invoke(
            null,
            [db, templateService, new EmptyToolCatalog(), new TestToolPermissionPolicy(), workspaceId, templateId, CancellationToken.None])!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var policy = (CapabilityPolicy?)result.GetType().GetProperty("Policy")!.GetValue(result);
        var toolDefinitions = (IReadOnlyList<LlmToolDefinition>?)result
            .GetType()
            .GetProperty("ToolDefinitions")!
            .GetValue(result);

        return new ResolvedCapabilitiesView(policy, toolDefinitions);
    }

    private static async Task<DbContextOptions<PlatformDbContext>> CreateDatabaseAsync(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        
        db.Capabilities.Add(new CapabilityEntity
        {
            CapabilityId = "cap-shell",
            Name = "Shell",
            Description = "Run shell command",
            ToolName = "shell",
            ToolDescription = "Execute a host shell command",
            ToolParametersJson = "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"Command to execute on the host\"},\"shell\":{\"type\":\"string\",\"description\":\"Shell mode: auto, wsl, bash, cmd, or powershell. Default: auto\"},\"working_directory\":{\"type\":\"string\",\"description\":\"Host working directory. Default: current runtime directory\"},\"timeout_seconds\":{\"type\":\"integer\",\"description\":\"Timeout in seconds, 1-600. Default: 30\"}},\"required\":[\"command\"]}",
            RequiresShellExecution = true,
            RequiresFileWrite = false,
            RequiresNetworkAccess = false,
            IsEnabled = true,
            SortOrder = 10,
        });
        await db.SaveChangesAsync();
        return options;
    }

    private static AgentTemplateFileService CreateFileService(
        string root,
        DbContextOptions<PlatformDbContext> options)
    {
        using var avatarFixture = new AvatarCatalogTestFixture();
        var avatarCatalog = avatarFixture.Catalog;
        return new AgentTemplateFileService(
            PuddingDataPaths.FromRoot(root),
            avatarCatalog,
            NullLogger<AgentTemplateFileService>.Instance);
    }

    private static UpsertGlobalAgentTemplateRequest CreateGlobalTemplateRequest(
        string? preferredProviderId = null,
        string? preferredModelId = null,
        string? memoryLlmProviderId = null,
        string? memoryLlmModelId = null,
        List<string>? selectedCapabilityIds = null) =>
        new(
            TemplateId: "general-assistant",
            Name: "General Assistant",
            Description: "template description",
            Role: "Service",
            SystemPrompt: null,
            UserPromptTemplate: null,
            PreferredProviderId: preferredProviderId,
            PreferredModelId: preferredModelId,
            MaxContextTokens: 8192,
            MaxReplyTokens: 2048,
            ContainerImage: null,
            SelectedCapabilityIds: selectedCapabilityIds ?? ["cap-shell"],
            SelectedSkillPackageIds: [],
            IsEnabled: true,
            SortOrder: 10,
            PersonaPrompt: null,
            ToolsDescription: null,
            BootstrapTemplate: null,
            AvatarId: "avatar-neutral",
            MemoryLlmProviderId: memoryLlmProviderId,
            MemoryLlmModelId: memoryLlmModelId,
            MemorySearchMode: "deep",
            ReasoningEffort: null,
            MaxRounds: 200,
            MaxElapsedSeconds: 1200,
            MaxToolCallsTotal: 100,
            ConsciousProfileId: "default-conscious",
            SubconsciousProfileId: "default-subconscious",
            AgentsPrompt: null,
            MemoryPrompt: null);

    private static PuddingLlmProvidersConfig CreateLlmConfigWithSubconsciousRole() => new()
    {
        Providers =
        [
            new PuddingLlmProviderConfig
            {
                ProviderId = "memory-provider",
                Name = "Memory Provider",
                BaseUrl = "https://memory.example/v1",
                ApiKey = "memory-key",
                Models =
                [
                    new PuddingLlmModelConfig
                    {
                        ModelId = "memory-model",
                        Name = "Memory Model",
                        IsDefault = true,
                        SortOrder = 1,
                    },
                ],
            },
        ],
        Profiles = new Dictionary<string, PuddingLlmProfileConfig>
        {
            ["default-subconscious"] = new()
            {
                ProviderId = "memory-provider",
                ModelId = "memory-model",
            },
        },
        Roles = new PuddingLlmRoleConfig
        {
            Subconscious = "default-subconscious",
        },
    };

    private sealed record ResolvedCapabilitiesView(
        CapabilityPolicy? Policy,
        IReadOnlyList<LlmToolDefinition>? ToolDefinitions);

    private sealed class EmptyToolCatalog : IPuddingToolCatalogService
    {
        public IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false) =>
        [
            new ToolDescriptor
            {
                ToolId = "shell",
                Name = "Shell",
                Description = "Execute shell command",
                PermissionLevel = ToolPermissionLevel.High,
                Safety = ToolSafetyFlags.RequiresShell,
            },
            new ToolDescriptor
            {
                ToolId = "file_patch",
                Name = "Patch file",
                Description = "Apply text patches to a file",
                PermissionLevel = ToolPermissionLevel.High,
                Safety = ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive,
                Parameters = new ToolParameterSchema(
                    [new ToolParameter("path", "string", "File path")],
                    ["path"]),
            },
        ];
    }

    private sealed class TestToolPermissionPolicy : IToolPermissionPolicyService
    {
        public ToolPermissionDecision Classify(ToolDescriptor descriptor) => new()
        {
            ToolId = descriptor.ToolId,
            Tier = ToolPermissionTier.RuntimeGranted,
            IsExposedToAgent = true,
            RequiresRuntimeAuthorization = true,
            RequiresShellExecution = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell),
            RequiresFileWrite = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite),
            RequiresNetworkAccess = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork),
        };

        public bool RequiresRuntimeAuthorization(ToolDescriptor descriptor)
            => Classify(descriptor).RequiresRuntimeAuthorization;

        public bool CanExposeToAgent(ToolDescriptor descriptor, CapabilityPolicy? policy) => true;

        public CapabilityPolicy BuildCapabilityPolicy(
            IEnumerable<ToolDescriptor> descriptors,
            IEnumerable<string> selectedToolNames,
            bool isTaskRole)
        {
            var selected = selectedToolNames.ToList();
            return new CapabilityPolicy
            {
                AllowShellExecution = selected.Contains("shell", StringComparer.OrdinalIgnoreCase),
                AllowedToolNames = selected,
                RequiresGrantToolNames = selected,
            };
        }
    }

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
                "pudding-chat-template-resolution-tests",
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
