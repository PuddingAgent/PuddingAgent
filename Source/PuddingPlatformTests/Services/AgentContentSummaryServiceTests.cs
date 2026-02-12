using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentContentSummaryServiceTests
{
    [TestMethod]
    public async Task UpdateAsync_WritesContentSummaryAndMetadata()
    {
        using var temp = new TempDataRoot();
        var text = new RecordingTextProcessingService("## 当前进展\n- 完成每日摘要服务。");
        var service = new AgentContentSummaryService(temp.Paths, text);
        var config = new MemoryLlmConfig("https://memory.local", "key", "model");

        var result = await service.UpdateAsync(new AgentContentSummaryUpdateRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            SessionId: "session-1",
            Day: "2026-06-16",
            ConversationText: "[user] 继续\n[assistant] 完成每日摘要服务",
            Reason: "manual_compact",
            MemoryLlmConfig: config));

        Assert.IsFalse(result.ResetForNewDay);
        Assert.IsTrue(File.Exists(result.ContentPath));
        StringAssert.Contains(await File.ReadAllTextAsync(result.ContentPath), "完成每日摘要服务");
        Assert.AreEqual(1, text.CurrentSessionCallCount);
        Assert.AreSame(config, text.LastCurrentSessionRequest!.MemoryLlmConfig);
        Assert.AreEqual("manual_compact", text.LastCurrentSessionRequest!.Reason);

        var metadata = await service.ReadMetadataAsync("agent-1");
        Assert.IsNotNull(metadata);
        Assert.AreEqual("2026-06-16", metadata.Day);
        Assert.AreEqual("session-1", metadata.LastSessionId);
    }

    [TestMethod]
    public async Task UpdateAsync_IncludesExistingSameDaySummary_WhenRollingForward()
    {
        using var temp = new TempDataRoot();
        var text = new RecordingTextProcessingService("## 当前进展\n- 保留旧事项并加入新事项。");
        var service = new AgentContentSummaryService(temp.Paths, text);

        await service.UpdateAsync(new AgentContentSummaryUpdateRequest(
            "workspace-1",
            "agent-1",
            "template-1",
            "session-1",
            "2026-06-16",
            "旧事项",
            "auto_compact",
            MemoryLlmConfig: null));

        await service.UpdateAsync(new AgentContentSummaryUpdateRequest(
            "workspace-1",
            "agent-1",
            "template-1",
            "session-2",
            "2026-06-16",
            "新事项",
            "auto_compact",
            MemoryLlmConfig: null));

        Assert.AreEqual(2, text.CurrentSessionCallCount);
        StringAssert.Contains(text.LastCurrentSessionRequest!.ConversationText, "已有当天滚动摘要");
        StringAssert.Contains(text.LastCurrentSessionRequest!.ConversationText, "新事项");
    }

    [TestMethod]
    public async Task UpdateAsync_ResetsContent_WhenDayChanges()
    {
        using var temp = new TempDataRoot();
        var text = new RecordingTextProcessingService("## 当前进展\n- 今天的新事项。");
        var service = new AgentContentSummaryService(temp.Paths, text);

        await service.UpdateAsync(new AgentContentSummaryUpdateRequest(
            "workspace-1",
            "agent-1",
            "template-1",
            "session-1",
            "2026-06-15",
            "昨天事项",
            "auto_compact",
            MemoryLlmConfig: null));

        var result = await service.UpdateAsync(new AgentContentSummaryUpdateRequest(
            "workspace-1",
            "agent-1",
            "template-1",
            "session-2",
            "2026-06-16",
            "今天事项",
            "auto_compact",
            MemoryLlmConfig: null));

        Assert.IsTrue(result.ResetForNewDay);
        Assert.IsFalse(text.LastCurrentSessionRequest!.ConversationText.Contains("已有当天滚动摘要", StringComparison.Ordinal));
        Assert.IsFalse(text.LastCurrentSessionRequest!.ConversationText.Contains("昨天事项", StringComparison.Ordinal));
        Assert.AreEqual("2026-06-16", (await service.ReadMetadataAsync("agent-1"))!.Day);
    }

    [TestMethod]
    public async Task SaveCompressedSummaryAsync_RollsSameDayAndResetsNextDay()
    {
        using var temp = new TempDataRoot();
        var text = new RecordingTextProcessingService("unused");
        var service = new AgentContentSummaryService(temp.Paths, text);

        await service.SaveCompressedSummaryAsync(new AgentCompressedContentSummaryRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            SessionId: "session-1",
            Day: "2026-06-16",
            SummaryMarkdown: "## session-1\n- 旧压缩摘要",
            Reason: "manual_compact"));

        await service.SaveCompressedSummaryAsync(new AgentCompressedContentSummaryRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            SessionId: "session-2",
            Day: "2026-06-16",
            SummaryMarkdown: "## session-2\n- 新压缩摘要",
            Reason: "manual_compact"));

        var sameDay = await File.ReadAllTextAsync(temp.Paths.AgentInstanceContentSummaryFile("agent-1"));
        StringAssert.Contains(sameDay, "旧压缩摘要");
        StringAssert.Contains(sameDay, "新压缩摘要");
        Assert.AreEqual(0, text.CurrentSessionCallCount);

        var result = await service.SaveCompressedSummaryAsync(new AgentCompressedContentSummaryRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            SessionId: "session-3",
            Day: "2026-06-17",
            SummaryMarkdown: "## session-3\n- 次日摘要",
            Reason: "auto_compact"));

        var nextDay = await File.ReadAllTextAsync(temp.Paths.AgentInstanceContentSummaryFile("agent-1"));
        Assert.IsTrue(result.ResetForNewDay);
        StringAssert.Contains(nextDay, "次日摘要");
        Assert.IsFalse(nextDay.Contains("旧压缩摘要", StringComparison.Ordinal));
    }

    private sealed class RecordingTextProcessingService(string summary) : ISubconsciousTextProcessingService
    {
        public int CurrentSessionCallCount { get; private set; }
        public CurrentSessionSummaryRequest? LastCurrentSessionRequest { get; private set; }

        public Task<string> SummarizeDailyLogAsync(DailyLogSummaryRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> SummarizeCurrentSessionAsync(CurrentSessionSummaryRequest request, CancellationToken ct = default)
        {
            CurrentSessionCallCount++;
            LastCurrentSessionRequest = request;
            return Task.FromResult(summary);
        }

        public Task<string> CompressConversationAsync(ConversationCompressionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-content-summary-tests", Guid.NewGuid().ToString("N"));
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
