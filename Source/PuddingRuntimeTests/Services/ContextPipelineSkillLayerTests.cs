using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextPipelineSkillLayerTests
{
    [TestMethod]
    public async Task AssembleAsync_Includes_Agent_Private_Skill_Index_Without_Full_Content()
    {
        using var temp = new TempDataRoot();
        var skillService = new AgentSkillFileService(temp.Paths);
        await skillService.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "daily_notes",
            Name = "Daily Notes",
            Version = "1.2.3",
            Summary = "Use this when writing daily notes.",
            Tags = ["notes", "workflow"],
            SkillMarkdown = "# Daily Notes\n\nFULL_SECRET_BODY_SHOULD_NOT_ENTER_CONTEXT",
        });
        await skillService.CreateAsync("agent-2", new AgentSkillCreateRequest
        {
            SkillId = "other_agent_skill",
            Name = "Other Agent Skill",
            Summary = "Must not leak.",
            SkillMarkdown = "OTHER_AGENT_SECRET",
        });
        var pipeline = CreatePipeline(new ContextAssemblyStore(), skillService);

        var result = await pipeline.AssembleAsync(CreateRequest("agent-1"), CancellationToken.None);

        StringAssert.Contains(result.SystemPrompt, "--- LAYER: SKILLS ---");
        StringAssert.Contains(result.UserContextPrefix, "Runtime-private SKILL index:");
        StringAssert.Contains(result.UserContextPrefix, "`daily_notes`");
        // 2026-08-22 冗余治理：索引行压缩为 skillId + 首句摘要 + 有限 tags/keywords；
        // Name/版本/path 不再进入索引（完整内容由 agent_skill 渐进加载）。
        StringAssert.Contains(result.UserContextPrefix, "Use this when writing daily notes.");
        StringAssert.Contains(result.UserContextPrefix, "tags=notes, workflow");
        Assert.IsFalse((result.SystemPrompt + result.UserContextPrefix).Contains("v1.2.3", StringComparison.Ordinal));
        Assert.IsFalse((result.SystemPrompt + result.UserContextPrefix).Contains("path=skills/daily_notes", StringComparison.Ordinal));
        Assert.IsFalse((result.SystemPrompt + result.UserContextPrefix).Contains("FULL_SECRET_BODY_SHOULD_NOT_ENTER_CONTEXT", StringComparison.Ordinal));
        Assert.IsFalse((result.SystemPrompt + result.UserContextPrefix).Contains("other_agent_skill", StringComparison.Ordinal));
        Assert.IsFalse((result.SystemPrompt + result.UserContextPrefix).Contains("OTHER_AGENT_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AssembleAsync_Produces_Stable_Skill_Index_Layer_For_Unchanged_Index()
    {
        using var temp = new TempDataRoot();
        var skillService = new AgentSkillFileService(temp.Paths);
        await skillService.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "stable_skill",
            Name = "Stable Skill",
            Summary = "Stable summary.",
            SkillMarkdown = "Initial body.",
        });
        var pipeline = CreatePipeline(new ContextAssemblyStore(), skillService);
        var request = CreateRequest("agent-1");

        var first = await pipeline.AssembleAsync(request, CancellationToken.None);
        var second = await pipeline.AssembleAsync(request, CancellationToken.None);

        Assert.AreEqual(
            ExtractLayer(first.SystemPrompt, "--- LAYER: SKILLS ---", "--- LAYER: WORKSPACE ENVIRONMENT ---"),
            ExtractLayer(second.SystemPrompt, "--- LAYER: SKILLS ---", "--- LAYER: WORKSPACE ENVIRONMENT ---"));
    }

    [TestMethod]
    public async Task AssembleAsync_UsesPersistentConfigurationAgentWithoutCreatingTransientDirectory()
    {
        using var temp = new TempDataRoot();
        var skillService = new AgentSkillFileService(temp.Paths);
        await skillService.CreateAsync("persistent-agent", new AgentSkillCreateRequest
        {
            SkillId = "persistent_skill",
            Name = "Persistent Skill",
            Summary = "Owned by the configured Agent.",
            SkillMarkdown = "# Persistent Skill",
        });
        var pipeline = CreatePipeline(new ContextAssemblyStore(), skillService);
        var transientId = "parent-session-sub-1234abcd";
        var request = CreateRequest(transientId) with
        {
            ConfigurationAgentInstanceId = "persistent-agent",
        };

        var result = await pipeline.AssembleAsync(request, CancellationToken.None);

        StringAssert.Contains(result.UserContextPrefix, "`persistent_skill`");
        Assert.IsFalse(Directory.Exists(temp.Paths.AgentInstanceRoot(transientId)));
    }

    [TestMethod]
    public async Task CatalogUpdate_PreservesSystemAndHistory_AcrossTurnsAndColdAssembly()
    {
        using var temp = new TempDataRoot();
        var skills = new AgentSkillFileService(temp.Paths);
        await skills.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "original_skill", Name = "Original", Summary = "Original summary.", SkillMarkdown = "body",
        });
        var pipeline = CreatePipeline(new ContextAssemblyStore(), skills);
        var request = CreateRequest("agent-1");
        var first = await pipeline.AssembleAsync(request, CancellationToken.None);
        var originalUser = new ChatMessage(ChatRole.User, first.UserContextPrefix + "\n" + request.UserMessage);
        var history = new List<ChatMessage> { new(ChatRole.System, first.SystemPrompt), originalUser };
        var warm = await pipeline.AssembleAsync(request with { SessionHistory = history, IsFirstMessage = false }, CancellationToken.None);
        Assert.IsFalse((warm.UserContextPrefix ?? "").Contains("RUNTIME-CATALOG"));
        Assert.AreEqual(first.SystemPrompt, warm.SystemPrompt, "Catalog deduplication must not change the pinned-memory trim budget.");

        await skills.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "new_skill", Name = "New", Summary = "New required capability.", SkillMarkdown = "new body",
        });
        var updated = await pipeline.AssembleAsync(request with { SessionHistory = history, IsFirstMessage = false }, CancellationToken.None);
        Assert.AreEqual(first.SystemPrompt, updated.SystemPrompt, "Background updates must not invalidate the system prefix.");
        StringAssert.Contains(updated.UserContextPrefix, "`new_skill`");
        Assert.AreSame(originalUser, history[1], "Earlier model-visible messages must remain untouched.");
        history.Add(new(ChatRole.User, updated.UserContextPrefix + "\nnext"));

        // Recreate the assembler (restart), using recovered model-visible history.
        var restarted = CreatePipeline(new ContextAssemblyStore(), new AgentSkillFileService(temp.Paths));
        var recovered = await restarted.AssembleAsync(request with { SessionHistory = history, IsFirstMessage = false }, CancellationToken.None);
        Assert.AreEqual(first.SystemPrompt, recovered.SystemPrompt);
        Assert.IsFalse((recovered.UserContextPrefix ?? "").Contains("RUNTIME-CATALOG"));

        // A -> B -> A must emit the removal, even though old A is still visible.
        await skills.SetEnabledAsync("agent-1", "new_skill", false);
        var removed = await pipeline.AssembleAsync(request with { SessionHistory = history, IsFirstMessage = false }, CancellationToken.None);
        StringAssert.Contains(removed.UserContextPrefix, "RUNTIME-CATALOG L9-SKILL-CATALOG");
        Assert.IsFalse(removed.UserContextPrefix!.Contains("`new_skill`"));
        Assert.AreEqual(first.SystemPrompt, removed.SystemPrompt);

        // After compaction drops the actual catalogs, re-emit current facts, not a stale 'sent' flag.
        var compacted = await restarted.AssembleAsync(request with
        {
            SessionHistory = [new(ChatRole.System, first.SystemPrompt), new(ChatRole.User, "<compact_summary>work continues</compact_summary>")],
            IsFirstMessage = false,
        }, CancellationToken.None);
        StringAssert.Contains(compacted.UserContextPrefix, "`original_skill`");
        Assert.IsFalse(compacted.UserContextPrefix!.Contains("`new_skill`"));
        Assert.AreEqual(first.SystemPrompt, compacted.SystemPrompt);
    }

    [TestMethod]
    public void CatalogDedup_RequiresCompleteLatestBody_NotOnlyHashOrAssistantEcho()
    {
        var a = ContextPipeline.BuildCatalogUpdate([], "catalog A", "L9-TOOL-CATALOG")!;
        Assert.IsNotNull(ContextPipeline.BuildCatalogUpdate([new(ChatRole.Assistant, a)], "catalog A", "L9-TOOL-CATALOG"));
        Assert.IsNotNull(ContextPipeline.BuildCatalogUpdate([new(ChatRole.User, a.Replace("catalog A", "truncated"))], "catalog A", "L9-TOOL-CATALOG"));
        Assert.IsNull(ContextPipeline.BuildCatalogUpdate([new(ChatRole.User, a)], "catalog A", "L9-TOOL-CATALOG"));
    }

    private static string ExtractLayer(string prompt, string startMarker, string endMarker)
    {
        var start = prompt.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        var end = prompt.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end <= start)
            Assert.Fail($"Expected marker '{endMarker}' after '{startMarker}'. start={start}, end={end}");
        return prompt[start..end];
    }

    private static ContextRequest CreateRequest(string agentInstanceId) => new()
    {
        Template = new AgentTemplateDefinition
        {
            TemplateId = "agent-template",
            Name = "Agent Template",
            TemplateType = AgentTemplateType.Task,
            SystemPrompt = "You are an agent.",
            Runtime = new RuntimeProfile { MaxContextTokens = 16000 },
        },
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentTemplateId = "agent-template",
        UserMessage = "What skills are available?",
        AgentInstanceId = agentInstanceId,
        IsFirstMessage = true,
    };

    private static ContextPipeline CreatePipeline(ContextAssemblyStore store, AgentSkillFileService skillService)
    {
        var memory = new FakeMemoryEngine();
        var importantMemory = new Mock<IImportantMemoryService>();
        importantMemory.Setup(x => x.ReadOrNull(It.IsAny<string>()))
            .Returns(string.Concat(Enumerable.Repeat("Stable pinned fact. ", 1000)));
        var skillRegistry = new AgentSkillPackageRegistry();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var skillRuntime = new SkillRuntime(Array.Empty<IAgentSkill>(), sandbox, NullLogger<SkillRuntime>.Instance);
        var workspaceProfile = new FakeWorkspaceProfileProvider();
        var promptBuilder = new SystemPromptBuilder(
            memory,
            skillRuntime,
            skillRegistry,
            NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo(),
            workspaceProfileProvider: workspaceProfile);

        return new ContextPipeline(
            memory,
            skillRuntime,
            skillRegistry,
            promptBuilder,
            new MemoryCache(new MemoryCacheOptions()),
            store,
            NullLogger<ContextPipeline>.Instance,
            new FakeExecutionEnvironmentProvider(),
            workspaceProfileProvider: workspaceProfile,
            agentSkillFileService: skillService,
            importantMemory: importantMemory.Object);
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-context-skill-layer-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId,
            string? workspaceId,
            string? agentId,
            string? parentSessionId = null) => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage,
            string workspaceId,
            string agentId,
            string? sessionId = null,
            int maxTokens = 2000,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply,
            string sessionId,
            string? workspaceId,
            string source,
            string? agentId = null,
            string? parentSessionId = null)
        {
        }

        public void ClearSession(string sessionId)
        {
        }
    }

    private sealed class FakeWorkspaceProfileProvider : IWorkspaceProfileProvider
    {
        public Task<string?> GetWorkspaceUserProfileAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeExecutionEnvironmentProvider : IExecutionEnvironmentProvider
    {
        public string OsDescription => "TestOS";
        public string OsArchitecture => "X64";
        public string RuntimeVersion => "10.0";
        public string AppBaseDirectory => "E:\\app";
        public string PathSeparator => "\\";
        public bool IsContainer => false;
        public string DefaultShell => "powershell";
        public string EnvironmentFingerprint => "test-env";
        public string? GetWorkspaceRoot(string workspaceId) => $"E:\\workspaces\\{workspaceId}";
    }
}
