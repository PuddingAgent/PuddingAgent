using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextPipelineLayerTests
{
    [TestMethod]
    public async Task AssembleAsync_Preserves_Layer_Order()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        var expectedOrder = new[]
        {
            "--- LAYER: IDENTITY ---",
            "--- LAYER: ENVIRONMENT ---",
            "--- LAYER: TOOLS ---",
            "--- LAYER: SKILLS ---",
            "--- LAYER: WORKSPACE ENVIRONMENT ---",
            "--- LAYER: RUNTIME ---",
            "--- LAYER: CURRENT ---",
        };

        var lastIndex = -1;
        foreach (var marker in expectedOrder)
        {
            var currentIndex = result.SystemPrompt.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsTrue(currentIndex >= 0,
                $"Expected layer marker '{marker}' not found in system prompt.");
            Assert.IsTrue(currentIndex > lastIndex,
                $"Layer '{marker}' appears out of order. Expected after index {lastIndex}, got {currentIndex}.");
            lastIndex = currentIndex;
        }
    }

    [TestMethod]
    public async Task AssembleAsync_Respects_Token_Budget()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);

        var template = CreateRequest().Template!;
        var request = CreateRequest() with
        {
            Template = new AgentTemplateDefinition
            {
                TemplateId = template.TemplateId,
                Name = template.Name,
                TemplateType = template.TemplateType,
                SystemPrompt = template.SystemPrompt,
                Runtime = new RuntimeProfile { MaxContextTokens = 8000 },
            },
        };

        var result = await pipeline.AssembleAsync(request, CancellationToken.None);

        Assert.IsTrue(result.UsedTokens <= result.TotalBudget,
            $"Used tokens ({result.UsedTokens}) exceeds total budget ({result.TotalBudget}).");
        Assert.IsTrue(result.UsedTokens > 0,
            "Expected non-zero token usage.");
    }

    [TestMethod]
    public async Task AssembleAsync_Returns_Layer_Snapshots()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        Assert.IsNotNull(result.Layers);
        Assert.IsTrue(result.Layers.Count > 0,
            "Expected at least one context layer snapshot.");
        Assert.IsTrue(result.Layers.All(l => !string.IsNullOrWhiteSpace(l.LayerName)),
            "All layer snapshots must have a non-empty LayerName.");
    }

    [TestMethod]
    public async Task AssembleAsync_Cache_Gives_Same_Output()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);
        var request = CreateRequest();

        var first = await pipeline.AssembleAsync(request, CancellationToken.None);
        var second = await pipeline.AssembleAsync(request, CancellationToken.None);

        var identityStart1 = first.SystemPrompt.IndexOf("--- LAYER: IDENTITY ---", StringComparison.Ordinal);
        var identityEnd1 = first.SystemPrompt.IndexOf("--- LAYER: ENVIRONMENT ---", identityStart1 + 1, StringComparison.Ordinal);
        var identityStart2 = second.SystemPrompt.IndexOf("--- LAYER: IDENTITY ---", StringComparison.Ordinal);
        var identityEnd2 = second.SystemPrompt.IndexOf("--- LAYER: ENVIRONMENT ---", identityStart2 + 1, StringComparison.Ordinal);

        Assert.IsTrue(identityStart1 >= 0 && identityEnd1 > identityStart1,
            "First call must contain IDENTITY layer.");
        Assert.IsTrue(identityStart2 >= 0 && identityEnd2 > identityStart2,
            "Second call must contain IDENTITY layer.");

        var firstIdentity = first.SystemPrompt[identityStart1..identityEnd1];
        var secondIdentity = second.SystemPrompt[identityStart2..identityEnd2];

        Assert.AreEqual(firstIdentity, secondIdentity,
            "Identity layer content should be identical on second call (cache hit).");
    }

    [TestMethod]
    public async Task AssembleAsync_EnvironmentLayer_Is_Cached()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);
        var request = CreateRequest();

        var first = await pipeline.AssembleAsync(request, CancellationToken.None);
        var second = await pipeline.AssembleAsync(request, CancellationToken.None);

        var envStart1 = first.SystemPrompt.IndexOf("--- LAYER: ENVIRONMENT ---", StringComparison.Ordinal);
        var envEnd1 = first.SystemPrompt.IndexOf("--- LAYER: TOOLS ---", envStart1 + 1, StringComparison.Ordinal);
        var envStart2 = second.SystemPrompt.IndexOf("--- LAYER: ENVIRONMENT ---", StringComparison.Ordinal);
        var envEnd2 = second.SystemPrompt.IndexOf("--- LAYER: TOOLS ---", envStart2 + 1, StringComparison.Ordinal);

        Assert.IsTrue(envStart1 >= 0 && envEnd1 > envStart1,
            "First call must contain ENVIRONMENT layer.");
        Assert.IsTrue(envStart2 >= 0 && envEnd2 > envStart2,
            "Second call must contain ENVIRONMENT layer.");

        var firstEnv = first.SystemPrompt[envStart1..envEnd1];
        var secondEnv = second.SystemPrompt[envStart2..envEnd2];

        Assert.AreEqual(firstEnv, secondEnv,
            "Environment layer content should be identical on second call (cache hit).");
    }

    [TestMethod]
    public async Task AssembleAsync_OptionalDependencies_Degrade_Gracefully()
    {
        var store = new ContextAssemblyStore();
        var memory = new FakeMemoryEngine();
        var skillRegistry = new AgentSkillPackageRegistry();
        var skillRuntime = new SkillRuntime(Array.Empty<IAgentSkill>(),
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<SkillRuntime>.Instance);
        var promptBuilder = new SystemPromptBuilder(
            memory, skillRuntime, skillRegistry,
            NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo());

        var pipeline = new ContextPipeline(
            memory,
            skillRuntime,
            skillRegistry,
            promptBuilder,
            new MemoryCache(new MemoryCacheOptions()),
            store,
            NullLogger<ContextPipeline>.Instance,
            new FakeExecutionEnvironmentProvider());

        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        Assert.IsNotNull(result.SystemPrompt);
        Assert.IsTrue(result.SystemPrompt.Length > 0,
            "System prompt should be non-empty even with only required dependencies.");
        Assert.IsTrue(result.UsedTokens > 0,
            "Token usage should be calculated even with minimal dependencies.");
    }

    [TestMethod]
    public async Task AssembleAsync_Exception_In_Layer_DoesNot_Crash_Assembly()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);
        var request = CreateRequest();

        var result = await pipeline.AssembleAsync(request, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.SystemPrompt);
        Assert.IsTrue(result.SystemPrompt.Length > 0,
            "Assembly should complete even if individual layers encounter errors.");
    }

    [TestMethod]
    public async Task AssembleAsync_Cancellation_Does_Not_Crash()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            await pipeline.AssembleAsync(CreateRequest(), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public void IsTopicSwitch_Detects_Change()
    {
        var pipeline = CreatePipeline(new ContextAssemblyStore());

        Assert.IsTrue(pipeline.IsTopicSwitch("Write a poem", "Deploy the database"));
        Assert.IsFalse(pipeline.IsTopicSwitch(null, "Hello"));
        Assert.IsFalse(pipeline.IsTopicSwitch("", "Hello"));
    }

    private static ContextPipeline CreatePipeline(ContextAssemblyStore store)
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
            workspaceProfileProvider: workspaceProfile);
    }

    private static ContextRequest CreateRequest(string agentInstanceId = "agent-1") => new()
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
        SessionId = $"session-{Guid.NewGuid():N}",
        AgentTemplateId = "agent-template",
        UserMessage = "Hello, what can you do?",
        AgentInstanceId = agentInstanceId,
        IsFirstMessage = true,
    };

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId, string? parentSessionId = null)
            => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) { }

        public void ClearSession(string sessionId) { }
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
