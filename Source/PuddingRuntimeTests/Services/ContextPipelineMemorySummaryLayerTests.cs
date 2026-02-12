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
public sealed class ContextPipelineMemorySummaryLayerTests
{
    [TestMethod]
    public async Task AssembleAsync_Includes_Recent_Daily_Summaries_And_Current_Content_On_First_Message()
    {
        using var temp = new TempDataRoot();
        var clockNow = new DateTimeOffset(2026, 6, 16, 9, 30, 0, TimeSpan.FromHours(8));
        var agentId = "agent-1";
        var yesterday = clockNow.AddDays(-1).ToString("yyyy-MM-dd");
        var dayBefore = clockNow.AddDays(-2).ToString("yyyy-MM-dd");
        Directory.CreateDirectory(temp.Paths.AgentInstanceDailySummaryRoot(agentId));
        await File.WriteAllTextAsync(
            Path.Combine(temp.Paths.AgentInstanceDailySummaryRoot(agentId), $"{yesterday}.md"),
            "昨天完成了 SKILL 文件系统服务。");
        await File.WriteAllTextAsync(
            Path.Combine(temp.Paths.AgentInstanceDailySummaryRoot(agentId), $"{dayBefore}.md"),
            "前天完成了日志目录迁移设计。");
        Directory.CreateDirectory(temp.Paths.AgentInstanceMemoryRoot(agentId));
        await File.WriteAllTextAsync(temp.Paths.AgentInstanceContentSummaryFile(agentId), "今天正在接入上下文摘要记忆。");
        await File.WriteAllTextAsync(
            Path.Combine(temp.Paths.AgentInstanceMemoryRoot(agentId), "content.meta.json"),
            $$"""
            {
              "agentInstanceId": "{{agentId}}",
              "day": "{{clockNow.ToString("yyyy-MM-dd")}}",
              "lastSessionId": "session-1",
              "lastReason": "session_compaction",
              "sourceHash": "hash",
              "updatedAt": "{{clockNow:O}}"
            }
            """);
        var memorySummaryBuilder = new AgentMemorySummaryContextBuilder(temp.Paths, () => clockNow);
        var pipeline = CreatePipeline(new ContextAssemblyStore(), memorySummaryBuilder);

        var result = await pipeline.AssembleAsync(CreateRequest(agentId, isFirstMessage: true), CancellationToken.None);

        StringAssert.Contains(result.SystemPrompt, "--- LAYER: AGENT MEMORY SUMMARY ---");
        StringAssert.Contains(result.SystemPrompt, "Recent daily summaries:");
        StringAssert.Contains(result.SystemPrompt, $"## {yesterday}");
        StringAssert.Contains(result.SystemPrompt, "昨天完成了 SKILL 文件系统服务。");
        StringAssert.Contains(result.SystemPrompt, $"## {dayBefore}");
        StringAssert.Contains(result.SystemPrompt, "前天完成了日志目录迁移设计。");
        StringAssert.Contains(result.SystemPrompt, "Current day rolling summary (content.md):");
        StringAssert.Contains(result.SystemPrompt, "今天正在接入上下文摘要记忆。");

        var skillsIndex = result.SystemPrompt.IndexOf("--- LAYER: SKILLS ---", StringComparison.Ordinal);
        var memoryIndex = result.SystemPrompt.IndexOf("--- LAYER: AGENT MEMORY SUMMARY ---", StringComparison.Ordinal);
        var userIndex = result.SystemPrompt.IndexOf("--- LAYER: USER ---", StringComparison.Ordinal);
        Assert.IsTrue(skillsIndex >= 0);
        Assert.IsTrue(memoryIndex > skillsIndex);
        Assert.IsTrue(userIndex > memoryIndex);
    }

    [TestMethod]
    public async Task AssembleAsync_Does_Not_Include_Memory_Summary_When_Message_Is_Not_First()
    {
        using var temp = new TempDataRoot();
        var clockNow = new DateTimeOffset(2026, 6, 16, 9, 30, 0, TimeSpan.FromHours(8));
        var agentId = "agent-1";
        Directory.CreateDirectory(temp.Paths.AgentInstanceDailySummaryRoot(agentId));
        await File.WriteAllTextAsync(
            Path.Combine(temp.Paths.AgentInstanceDailySummaryRoot(agentId), $"{clockNow.AddDays(-1):yyyy-MM-dd}.md"),
            "不应该出现在非首轮上下文。");
        var memorySummaryBuilder = new AgentMemorySummaryContextBuilder(temp.Paths, () => clockNow);
        var pipeline = CreatePipeline(new ContextAssemblyStore(), memorySummaryBuilder);

        var result = await pipeline.AssembleAsync(CreateRequest(agentId, isFirstMessage: false), CancellationToken.None);

        Assert.IsFalse(result.SystemPrompt.Contains("--- LAYER: AGENT MEMORY SUMMARY ---", StringComparison.Ordinal));
        Assert.IsFalse(result.SystemPrompt.Contains("不应该出现在非首轮上下文。", StringComparison.Ordinal));
    }

    private static ContextRequest CreateRequest(string agentInstanceId, bool isFirstMessage) => new()
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
        UserMessage = "What should we continue?",
        AgentInstanceId = agentInstanceId,
        IsFirstMessage = isFirstMessage,
    };

    private static ContextPipeline CreatePipeline(
        ContextAssemblyStore store,
        AgentMemorySummaryContextBuilder memorySummaryBuilder)
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
            agentMemorySummaryContextBuilder: memorySummaryBuilder);
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-context-memory-summary-tests", Guid.NewGuid().ToString("N"));
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
