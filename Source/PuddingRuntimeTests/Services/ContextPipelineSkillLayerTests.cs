using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
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
        StringAssert.Contains(result.SystemPrompt, "Runtime-private SKILL index:");
        StringAssert.Contains(result.SystemPrompt, "`daily_notes`");
        StringAssert.Contains(result.SystemPrompt, "Daily Notes");
        StringAssert.Contains(result.SystemPrompt, "v1.2.3");
        StringAssert.Contains(result.SystemPrompt, "Use this when writing daily notes.");
        StringAssert.Contains(result.SystemPrompt, "tags=notes, workflow");
        StringAssert.Contains(result.SystemPrompt, "path=skills/daily_notes");
        Assert.IsFalse(result.SystemPrompt.Contains("FULL_SECRET_BODY_SHOULD_NOT_ENTER_CONTEXT", StringComparison.Ordinal));
        Assert.IsFalse(result.SystemPrompt.Contains("other_agent_skill", StringComparison.Ordinal));
        Assert.IsFalse(result.SystemPrompt.Contains("OTHER_AGENT_SECRET", StringComparison.Ordinal));
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
            agentSkillFileService: skillService);
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
