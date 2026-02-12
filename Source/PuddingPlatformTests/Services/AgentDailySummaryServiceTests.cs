using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentDailySummaryServiceTests
{
    [TestMethod]
    public async Task GenerateAsync_ReadsOrdinaryMessageLogs_WritesDailySummaryAndIndex()
    {
        using var temp = new TempDataRoot();
        var dayRoot = temp.Paths.AgentInstanceMessageLogDayRoot("agent-1", "2026-06-16");
        Directory.CreateDirectory(dayRoot);
        await File.WriteAllTextAsync(Path.Combine(dayRoot, "session-a.md"), "## session-a\n\n[user] 继续阶段 2\n");
        await File.WriteAllTextAsync(Path.Combine(dayRoot, "session-b.md"), "## session-b\n\n[agent] 完成潜意识文本处理服务\n");

        var text = new RecordingTextProcessingService("## 2026-06-16\n- 完成潜意识文本处理服务。");
        var service = new AgentDailySummaryService(temp.Paths, text);
        var config = new MemoryLlmConfig("https://memory.local", "key", "model");

        var result = await service.GenerateAsync(new AgentDailySummaryGenerateRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            Day: "2026-06-16",
            MemoryLlmConfig: config));

        Assert.IsFalse(result.Skipped);
        Assert.IsTrue(File.Exists(result.SummaryPath));
        StringAssert.Contains(await File.ReadAllTextAsync(result.SummaryPath), "完成潜意识文本处理服务");
        Assert.AreEqual(1, text.DailyCallCount);
        Assert.AreSame(config, text.LastDailyRequest!.MemoryLlmConfig);
        StringAssert.Contains(text.LastDailyRequest!.OrdinaryLogMarkdown, "session-a");
        StringAssert.Contains(text.LastDailyRequest!.OrdinaryLogMarkdown, "session-b");

        var indexPath = temp.Paths.AgentInstanceMemoryIndexFile("agent-1");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        var root = doc.RootElement;
        Assert.AreEqual("agent-1", root.GetProperty("agentInstanceId").GetString());
        var summary = root.GetProperty("dailySummaries")[0];
        Assert.AreEqual("2026-06-16", summary.GetProperty("day").GetString());
        Assert.AreEqual(result.SourceHash, summary.GetProperty("sourceHash").GetString());
        Assert.AreEqual(2, summary.GetProperty("sourceSessionIds").GetArrayLength());
    }

    [TestMethod]
    public async Task GenerateAsync_SkipsLlmCall_WhenSourceHashUnchanged()
    {
        using var temp = new TempDataRoot();
        var dayRoot = temp.Paths.AgentInstanceMessageLogDayRoot("agent-1", "2026-06-16");
        Directory.CreateDirectory(dayRoot);
        await File.WriteAllTextAsync(Path.Combine(dayRoot, "session-a.md"), "## session-a\n\n[user] hello\n");

        var text = new RecordingTextProcessingService("## summary\n- hello");
        var service = new AgentDailySummaryService(temp.Paths, text);
        var request = new AgentDailySummaryGenerateRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            Day: "2026-06-16",
            MemoryLlmConfig: null);

        var first = await service.GenerateAsync(request);
        var second = await service.GenerateAsync(request);

        Assert.IsFalse(first.Skipped);
        Assert.IsTrue(second.Skipped);
        Assert.AreEqual(first.SourceHash, second.SourceHash);
        Assert.AreEqual(1, text.DailyCallCount);
    }

    [TestMethod]
    public async Task GenerateForDayAsync_DiscoversAgentMessageLogs_AndGeneratesSummaries()
    {
        using var temp = new TempDataRoot();
        var manifestRoot = temp.Paths.AgentInstanceRoot("agent-1");
        Directory.CreateDirectory(manifestRoot);
        await File.WriteAllTextAsync(Path.Combine(manifestRoot, "manifest.json"), """
        {
          "agentInstanceId": "agent-1",
          "templateId": "template-1",
          "workspaceId": "workspace-1"
        }
        """);

        var dayRoot = temp.Paths.AgentInstanceMessageLogDayRoot("agent-1", "2026-06-15");
        Directory.CreateDirectory(dayRoot);
        await File.WriteAllTextAsync(Path.Combine(dayRoot, "session-a.md"), "## session-a\n\n[user] yesterday work\n");

        var text = new RecordingTextProcessingService("## 2026-06-15\n- yesterday work");
        var resolver = new RecordingLlmConfigResolver();
        var summaryService = new AgentDailySummaryService(temp.Paths, text);
        var batch = new AgentDailySummaryBatchService(
            temp.Paths,
            summaryService,
            NullLogger<AgentDailySummaryBatchService>.Instance,
            resolver);

        var results = await batch.GenerateForDayAsync("2026-06-15");

        Assert.AreEqual(1, results.Count);
        Assert.IsFalse(results[0].Skipped);
        Assert.IsTrue(File.Exists(temp.Paths.AgentInstanceMemoryIndexFile("agent-1")));
        Assert.AreEqual("template-1", resolver.LastTemplateId);
        Assert.AreEqual("workspace-1", resolver.LastWorkspaceId);
        Assert.AreEqual("memory-model", text.LastDailyRequest!.MemoryLlmConfig!.ModelId);
    }

    [TestMethod]
    public async Task GeneratePreviousDayAsync_UsesPreviousLocalDate()
    {
        using var temp = new TempDataRoot();
        var dayRoot = temp.Paths.AgentInstanceMessageLogDayRoot("agent-1", "2026-06-15");
        Directory.CreateDirectory(dayRoot);
        await File.WriteAllTextAsync(Path.Combine(dayRoot, "session-a.md"), "## session-a\n\n[user] yesterday work\n");

        var text = new RecordingTextProcessingService("## 2026-06-15\n- yesterday work");
        var summaryService = new AgentDailySummaryService(temp.Paths, text);
        var batch = new AgentDailySummaryBatchService(
            temp.Paths,
            summaryService,
            NullLogger<AgentDailySummaryBatchService>.Instance);

        var results = await batch.GeneratePreviousDayAsync(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.FromHours(8)));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("2026-06-15", results[0].Day);
    }

    private sealed class RecordingTextProcessingService(string dailySummary) : ISubconsciousTextProcessingService
    {
        public int DailyCallCount { get; private set; }
        public DailyLogSummaryRequest? LastDailyRequest { get; private set; }

        public Task<string> SummarizeDailyLogAsync(DailyLogSummaryRequest request, CancellationToken ct = default)
        {
            DailyCallCount++;
            LastDailyRequest = request;
            return Task.FromResult(dailySummary);
        }

        public Task<string> SummarizeCurrentSessionAsync(CurrentSessionSummaryRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> CompressConversationAsync(ConversationCompressionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLlmConfigResolver : ILLMConfigResolver
    {
        public string? LastTemplateId { get; private set; }
        public string? LastWorkspaceId { get; private set; }

        public Task<LlmRoutingConfig?> ResolveConsciousAsync(
            string templateId,
            string? workspaceId,
            CancellationToken ct = default) =>
            Task.FromResult<LlmRoutingConfig?>(null);

        public Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
            string templateId,
            string? workspaceId,
            CancellationToken ct = default)
        {
            LastTemplateId = templateId;
            LastWorkspaceId = workspaceId;
            return Task.FromResult<MemoryLlmRoutingConfig?>(new MemoryLlmRoutingConfig
            {
                Endpoint = "https://memory.local",
                ApiKey = "key",
                ModelId = "memory-model",
            });
        }
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-daily-summary-tests", Guid.NewGuid().ToString("N"));
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
}
