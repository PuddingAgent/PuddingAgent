using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingFullTextIndex.Contracts;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextPipelineAgentLogRecallLayerTests
{
    [TestMethod]
    public async Task AssembleAsync_Includes_Agent_Log_Recall_On_User_Message()
    {
        using var temp = new TempDataRoot();
        var agentId = "agent-1";
        var messageRoot = temp.Paths.AgentInstanceMessageLogsRoot(agentId);
        var dailyRoot = temp.Paths.AgentInstanceDailySummaryRoot(agentId);
        Directory.CreateDirectory(Path.Combine(messageRoot, "2026-06-15"));
        Directory.CreateDirectory(dailyRoot);
        var engine = new FakeFullTextSearchEngine
        {
            Results =
            {
                [messageRoot] =
                [
                    new FullTextSearchMatch(Path.Combine(messageRoot, "2026-06-15", "s1.md"), 2, "needle message recall")
                ],
                [dailyRoot] =
                [
                    new FullTextSearchMatch(Path.Combine(dailyRoot, "2026-06-10.md"), 1, "needle daily recall")
                ],
            },
        };
        var logRecallService = new AgentLogRecallService(
            temp.Paths,
            engine,
            () => new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store, logRecallService);

        var result = await pipeline.AssembleAsync(CreateRequest(agentId), CancellationToken.None);

        StringAssert.Contains(result.SystemPrompt, "--- LAYER: RECALLED ---");
        StringAssert.Contains(result.SystemPrompt, "[AGENT LOG RECALL]");
        StringAssert.Contains(result.SystemPrompt, "Recent 5 days message logs:");
        StringAssert.Contains(result.SystemPrompt, "needle message recall");
        StringAssert.Contains(result.SystemPrompt, "Recent 180 days daily summaries:");
        StringAssert.Contains(result.SystemPrompt, "needle daily recall");

        Assert.IsTrue(store.TryGet("session-1", out var snapshot));
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(
            snapshot!.Layers.Any(layer => layer.LayerName == "L6-AGENT-LOG-RECALL"),
            "Agent private log recall must be tracked as its own context layer for cache and recall observability.");
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
        UserMessage = "needle",
        AgentInstanceId = agentInstanceId,
        IsFirstMessage = false,
    };

    private static ContextPipeline CreatePipeline(
        ContextAssemblyStore store,
        AgentLogRecallService logRecallService)
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
            agentLogRecallService: logRecallService);
    }

    private sealed class FakeFullTextSearchEngine : IFullTextSearchEngine
    {
        public Dictionary<string, IReadOnlyList<FullTextSearchMatch>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasIndex(string directoryPath) => false;

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default)
        {
            var matches = Results.TryGetValue(directoryPath, out var value)
                ? value.Take(maxResults).ToList()
                : [];
            return Task.FromResult(new FullTextSearchResult(true, matches, null, matches.Count, 1));
        }

        public Task<FullTextIndexResult> BuildIndexAsync(
            string directoryPath,
            string? filePatterns = null,
            CancellationToken ct = default)
            => Task.FromResult(new FullTextIndexResult(true, 1, 1, 1, null));

        public bool RemoveIndex(string directoryPath) => true;
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-context-log-recall-tests", Guid.NewGuid().ToString("N"));
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
